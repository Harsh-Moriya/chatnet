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
| `postgres`, `redis` | With passwords, data in volumes, and no published ports. |
| `jaeger` | Only with `--profile tracing`. Its page listens on the machine's loopback address only. |

Start order: Postgres and Redis healthy, then `migrate` finishes, then the API instances become healthy (`/health`), then Caddy.

Containers run read-only with no capabilities and no new privileges, and the API runs as an unprivileged user. Files live in object storage that is **not** part of this stack.

## What you need

- A Linux machine with Docker and the Compose plugin, and ports 80 and 443 open to the internet. x86-64 and ARM64 both work: the images are built for the CPU of the machine that builds them, so the commands are the same on both. Allow enough memory for the build; on a machine with 2 GB of RAM, add swap.
- A domain name whose DNS records point at the machine. Caddy asks for the certificate as soon as it starts, so the records must be right before the first start.
- An S3-compatible bucket (Cloudflare R2, for example) with an access key.

## Prepare the bucket

The bucket needs two things, the same ones `docker/storage/init.sh` applies to the local object store:

1. A **CORS rule** that allows `PUT`, `GET` and `HEAD` from your site's origin and exposes the `ETag` header. Start from `docker/storage/cors.json` and replace `AllowedOrigins` with `https://<your domain>`.
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

## Trying it on one machine

Set `DOMAIN=localhost` in `.env.prod`. Caddy then uses its own certificate authority, which a browser does not trust, so the browser warns once; `curl -k https://localhost/health` shows the stack answering. To skip TLS altogether, also set `SITE_ADDRESS=http://localhost`. The development file and this stack use different Compose project names (`chatnet` and `chatnet-prod`), so they never share volumes.

## Web Push

Alerts for closed browsers need `PUSH_PUBLIC_KEY`, `PUSH_PRIVATE_KEY` and `PUSH_SUBJECT` in `.env.prod`; the recipe to generate the pair is in `.env.prod.example`. Without them the feature is off and nothing else changes. Changing the pair makes every browser subscribe again, which they do by themselves the next time the page is open. A browser opens the connection to its own vendor's push service, so the API container must reach `fcm.googleapis.com`, `updates.push.services.mozilla.com`, `push.apple.com` and `notify.windows.com` on port 443.

## Limits

- One Postgres and one Redis, with no replication and no automatic backups.
- The rate limits are counted in Redis. While Redis cannot be reached they are not applied (only the account lockout, in Postgres, still protects a password), and a Redis that loses its data starts the counts again from zero. Behind the proxy, an outage of Redis also takes the API out of the rotation within ten seconds, because `/health` includes Redis.
- Updates are not rolling: the API instances are replaced together.
- The migrations create two Postgres extensions, `btree_gin` and `pg_trgm`, for message search and the people search. Both ship with Postgres and are trusted, so the owner of the database may create them, which works with the Compose Postgres and with a managed one. The indexes are built inside the migration, which holds writes to the messages and users tables while they build: seconds on a small database, longer on a large one. Keep autovacuum on, which the search plans rely on.
- The Docker network is trusted as a whole: anything else started on it could claim a client address. `TRUSTED_PROXY_NETWORK` names the network Caddy reaches the API from.
