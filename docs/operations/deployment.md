# Deployment

`docker-compose.prod.yml` describes the whole application for **one machine**, for example a small VPS. It is a Compose file for a single host, not an orchestrator: there are no rolling updates and no replication of Postgres or Redis.

```mermaid
flowchart LR
    Browser -- "https 80, 443" --> Caddy
    Caddy -- "the page (built files)" --> Browser
    Caddy -- "/api /hubs /health" --> API1["api (instance 1)"]
    Caddy -- "/api /hubs /health" --> API2["api (instance 2)"]
    API1 --- Postgres[("postgres")]
    API2 --- Postgres
    API1 --- Redis[("redis")]
    API2 --- Redis
    API1 --- Storage[("S3-compatible storage<br/>outside this stack")]
    Migrate["migrate (one-shot job)"] --> Postgres
```

## What runs

| Service | Role |
|---|---|
| `caddy` | Serves the built frontend, passes `/api`, `/hubs` and `/health` to the API instances, and obtains and renews the HTTPS certificate. The only service reachable from outside (ports 80, 443 and 443/udp for HTTP/3). The page and the API share one address, so the refresh cookie stays first-party and no CORS is needed. |
| `api` | Two instances by default (`API_REPLICAS`). They share the work and reach each other through Redis, so a message sent through one reaches a browser connected to the other. Caddy finds the instances through DNS every 5 seconds, health-checks them, and keeps each browser on one instance with a cookie (SignalR needs that). |
| `migrate` | A one-shot job that applies the EF Core migrations and exits before the API instances start, so several instances starting together never change the schema at once. |
| `coturn` | The TURN server that relays a call when two browsers cannot reach each other. It runs on the machine's own network and not behind Caddy: 3478 (UDP and TCP) and the relay ports 49160 to 49459 (UDP) are open on the host. See [Calls](#calls-the-turn-server). |
| `postgres`, `redis` | With passwords, data in volumes, and no published ports. |
| `jaeger` | Only with `--profile tracing`. Its page listens on the machine's loopback address only. |

Start order: Postgres and Redis healthy, then `migrate` finishes, then the API instances become healthy (`/health`), then Caddy.

The API and Caddy containers drop all Linux capabilities and cannot gain new privileges. The API container also has a read-only filesystem and runs as an unprivileged user. Files live in object storage that is **not** part of this stack.

## What you need

- A Linux machine with Docker and the Compose plugin, and ports 80 and 443 open to the internet. x86-64 and ARM64 both work: the images are built for the CPU of the machine that builds them, so the commands are the same on both. Allow enough memory for the build; on a machine with 2 GB of RAM, add swap.
- For calls, ports 3478 (UDP and TCP) and 49160 to 49459 (UDP) open to the internet, in the machine's firewall and, on a cloud provider, in its security group (a single rule for the range).
- A domain name whose DNS records point at the machine. Caddy asks for the certificate as soon as it starts, so the records must be right before the first start.
- An S3-compatible bucket (Cloudflare R2, for example) with an access key.

## Prepare the bucket

The bucket needs two things, the same ones `docker/storage/init.sh` applies to the local object store:

1. A **CORS rule** that allows `PUT`, `GET` and `HEAD` from your site's origin and exposes the `ETag` header. The browser uploads and fetches files itself, so `GET` and `PUT` from the site's origin are both required. The video player reads a file in ranges: a request for a range of the form `bytes=start-end` goes without a preflight, but a rule that lists its allowed headers (R2's does) should include `Range` too, since a range of another form would need it. Start from `docker/storage/cors.json` and replace `AllowedOrigins` with `https://<your domain>`.
2. A **lifecycle rule** that expires objects under `pending/` after one day (`docker/storage/lifecycle.json`), so uploads that were never attached to a message do not stay.

Both are standard S3 API calls (`put-bucket-cors`, `put-bucket-lifecycle-configuration`).

## First start

```bash
git clone <this repository> chatnet && cd chatnet
cp .env.prod.example .env.prod
```

Fill in every value in `.env.prod`; the file explains each one (see also [configuration](configuration.md)). Then:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
docker compose -f docker-compose.prod.yml --env-file .env.prod ps
```

The first build takes a few minutes. When `ps` shows the API instances as healthy, open `https://<your domain>`. To follow the API's log (one JSON object per line):

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod logs -f api
```

## Updating

```bash
git pull
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
```

The migration job runs first and does nothing when the database is already up to date. The API instances are replaced together, so there is a short break while they start. Take a [backup](backups.md) before an update that includes migrations.

**Updating a database that holds messages from before they were sealed.** The migration that makes messages ciphertext deletes every message that exists, with its notifications and the pending pushes about them, because the server holds those in the clear and cannot seal them: only the sender's browser can. It cannot be undone, and rolling the migration back puts the columns back without the data. The files those messages carried are removed from the bucket by the outbox relay once the API runs, one row for each file. Take a dump first if the old messages matter, and keep in mind that the dump holds them in the clear: keep it only as long as it is needed and destroy it knowingly.

**What a deleted message leaves behind.** Deleted rows stay in the table's file as dead tuples until the database vacuums them, which autovacuum does within minutes for a table with many deleted rows and later for a small one (the attachment table kept the old file names until it was vacuumed). The write-ahead log and earlier dumps still hold the old text until they are recycled or destroyed. To clear the tables at once after the migration, run `VACUUM FULL "Messages"` and `VACUUM FULL "MessageAttachments"`.

**Updating a database that holds files from before they were encrypted.** The migration that encrypts files deletes every message that carries a file, with its notifications and the pending pushes about them, and removes the name, type and kind columns of the attachment table, because the server holds those in the clear and cannot encrypt a file: only the sender's browser can. It cannot be undone, and rolling it back puts the columns back without the data. The stored files are removed from the bucket by the outbox relay once the API runs, one row for each file. Messages with text only are kept.

## Trying it on one machine

Set `DOMAIN=localhost` in `.env.prod`. Caddy then uses its own certificate authority, which a browser does not trust, so the browser warns once; `curl -k https://localhost/health` shows the stack answering. To skip TLS altogether, also set `SITE_ADDRESS=http://localhost`. This stack is named `chatnet-prod`, so it never shares volumes with the development file, whose project name comes from the folder it is in.

## Calls (the TURN server)

Two browsers in a call send their audio and video to each other directly when they can. When neither can reach the other (each is behind a NAT that allows nothing in), the packets go through a **relay**, a TURN server, that both can reach. `coturn` is that relay. The media is encrypted between the two browsers (DTLS-SRTP), so the relay moves ciphertext and learns only who talks to whom, how much and when.

- **The secret.** `TURN_SECRET` in `.env.prod` (at least 32 characters, `openssl rand -hex 32`) is shared by the relay and the API. The API makes a credential for each person from it: a username of the time the credential ends and the person's id, and the HMAC-SHA1 of that username as the password (the scheme TURN servers call `use-auth-secret`). The relay checks it without a list of people and refuses a username whose time has passed. A credential lasts an hour (`Turn:CredentialLifetime`).
- **The address.** `TURN_EXTERNAL_IP` is `public address/private address` of the machine, such as `203.0.113.10/10.0.1.20`. On a cloud VM the public address is only forwarded to the machine, which has its own private one (`ip addr`); on AWS the public one is the Elastic IP. The relay needs both to tell a browser where to send, and it is also what lets two people who both need the relay reach each other through it.
- **The names.** The API hands out `turn:<DOMAIN>:3478` (UDP and TCP) and `stun:<DOMAIN>:3478`, so the site's own DNS record is all there is to set up.
- **What it refuses.** The relay forwards to the address a caller names, so it is not allowed to forward to private, loopback, link-local (the cloud metadata address among them), shared or multicast ranges (`docker/turn/hardened.conf`): without that a signed-in person could use it to reach the machine and the network behind it. It makes no TCP relays, has no admin console and no TLS listener, and runs read-only, unprivileged and without capabilities, with a 128 MB memory limit.
- **Limits.** Per credential 12 allocations, in all 300, and 4 Mbit/s for one allocation and 200 Mbit/s for all. A browser makes an allocation for every network interface it has (measured: two for a browser with two interfaces), and each takes a relay port, so the range holds 300. The bandwidth of the machine, not its memory, is what limits how many calls can be relayed at once; most calls go directly and never touch it.

To check that the relay answers and accepts a credential, make one on the machine (the private address is the second half of `TURN_EXTERNAL_IP`):

```bash
secret=$(grep '^TURN_SECRET=' .env.prod | cut -d= -f2-)
user="$(( $(date +%s) + 600 )):check"
pass=$(printf '%s' "$user" | openssl dgst -sha1 -hmac "$secret" -binary | openssl base64)
docker compose -f docker-compose.prod.yml --env-file .env.prod exec coturn \
  turnutils_uclient -u "$user" -w "$pass" -e <private address> -m 1 -n 1 -l 100 -p 3478 127.0.0.1
echo $?
```

Exit code 0 means the relay made an allocation. A wrong secret or an expired username ends with `Cannot complete Allocation` after about fifteen seconds and a non-zero code.

## Web Push

Alerts for closed browsers need `PUSH_PUBLIC_KEY`, `PUSH_PRIVATE_KEY` and `PUSH_SUBJECT` in `.env.prod`; the recipe to generate the pair is in `.env.prod.example`. Without them the feature is off and nothing else changes. Changing the pair makes every browser subscribe again, which they do by themselves the next time the page is open. A browser opens the connection to its own vendor's push service, so the API container must reach `fcm.googleapis.com`, `updates.push.services.mozilla.com`, `push.apple.com` and `notify.windows.com` on port 443.

## Email

Password reset needs a mail relay. Without `SMTP_HOST` in `.env.prod` the stack runs as before and asking for a reset link answers 503; links that were sent earlier still work. The variables are listed in `.env.prod.example`. Any SMTP relay works. For Resend:

- Verify the domain you send from at resend.com/domains and add the DNS records it generates (DKIM and SPF); a DMARC record is recommended. It suggests sending from a subdomain such as `auth.<your domain>`.
- Create an API key. Set `SMTP_HOST=smtp.resend.com`, `SMTP_USERNAME=resend`, `SMTP_PASSWORD=<the API key>`, and `MAIL_FROM_ADDRESS` to an address on the verified domain. Port 587 with `StartTls` is the default; port 465 needs `SMTP_SECURITY=SslOnConnect`.
- Keep open and click tracking off for that domain. Resend has them off by default and turns them on per domain. A tracked link goes through the provider's own address and carries the reset token with it.

The API container must reach the relay on its port, outbound. The reset link points at `https://<DOMAIN>/reset-password`. Mail is sent by the outbox relay within a few seconds of the request, and a request that waits more than ten minutes (the relay was down) is dropped.

## Link previews

A link to the site shows a title, a line and a picture on LinkedIn, Slack, WhatsApp and X. Those services fetch the page once and read the Open Graph tags in `frontend/index.html` (`og:title`, `og:description`, `og:url`, `og:image`, and `twitter:card` for X); they run no script, so the tags are in that file and not in the application. The picture is `frontend/public/og-image.png`, 1200 x 630 pixels. Both addresses in the tags are absolute and name the public domain: **change them in `index.html` when the domain changes**. `robots.txt` allows every crawler.

The tags reach the world when the web image is rebuilt and deployed (see [Updating](#updating)). A service keeps the preview it first made for a link, so a link that was shared before the tags existed shows nothing or the old preview until it is refreshed: for LinkedIn open the [Post Inspector](https://www.linkedin.com/post-inspector/), enter the address and press Inspect; it fetches the page again and the preview is renewed for new posts.

## Limits

- The relay does not speak TLS (`turns:`), so a network that lets out only port 443 cannot relay a call. At most about 300 relay ports are in use at once (see [Calls](#calls-the-turn-server)).
- One Postgres and one Redis, with no replication and no automatic backups.
- The rate limits are counted in Redis. While Redis cannot be reached they are not applied (only the account lockout, in Postgres, still protects a password), and a Redis that loses its data starts the counts again from zero. Behind the proxy, an outage of Redis also takes the API out of the rotation within ten seconds, because `/health` includes Redis.
- Updates are not rolling: the API instances are replaced together.
- The migrations create the Postgres extension `pg_trgm` for the people search. It ships with Postgres and is trusted, so the owner of the database may create it, which works with the Compose Postgres and with a managed one. The index is built inside the migration, which holds writes to the users table while it builds: seconds on a small database, longer on a large one. Keep autovacuum on, which the people search plan relies on.
- The Docker network is trusted as a whole: anything else started on it could claim a client address. `TRUSTED_PROXY_NETWORK` names the network Caddy reaches the API from.
