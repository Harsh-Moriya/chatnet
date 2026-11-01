# ChatNET

[![CI](https://github.com/Harsh-Moriya/chatnet/actions/workflows/ci.yml/badge.svg)](https://github.com/Harsh-Moriya/chatnet/actions/workflows/ci.yml)

A real-time, multi-user chat application. **ASP.NET Core (.NET 10)** and **SignalR** on the backend, **React + TypeScript** on the frontend, **PostgreSQL** for storage and **Redis** for the SignalR backplane.

Two kinds of conversation share one model: **direct** (1:1) and **group** (many members).

## Status

Under active development.

| Area | State |
|---|---|
| Registration and login (ASP.NET Identity, JWT) | Implemented |
| Conversation model: find-or-create direct chats, groups, user search | Implemented |
| SignalR hub with Redis backplane: live groups per conversation, typing indicators, new-conversation push | Implemented |
| Sending messages (REST) with live delivery to the conversation over SignalR, and the latest 50 messages as history | Implemented |
| Chat interface: conversation list, message view, composer, new direct or group chat, typing indicators, optimistic sending with retry, reconnecting | Implemented |
| Scrolling back through older messages (cursor pagination, oldest-first pages of 50) | Implemented |
| Catching up on messages missed while disconnected, with no gaps | Implemented |
| Unread counts, marking a conversation read, read receipts ("Seen") | Implemented |
| Online presence: a dot on direct chats, "N online" in groups, "last seen", correct across several server instances | Implemented |
| Sessions that renew themselves: 15-minute access tokens, rotating refresh tokens in an httpOnly cookie, sign-out that ends the session | Implemented |
| Retrying a send is safe: a message is stored once however many times the request arrives | Implemented |
| Editing and deleting messages: the sender edits for 15 minutes (marked "edited"), deletes at any time, and a group owner or admin can delete anyone's; everyone sees the change live and the files of a deleted message are removed | Implemented |
| Group management: add people, remove someone, make people admins, hand the group over, rename, leave. The owner can do everything and an admin can add people, remove members and rename; when the owner leaves the group goes to the admin who has been in longest. Someone who left keeps the conversation read-only up to when they left, and stops hearing it at once on every server | Implemented |
| Profiles and pictures: change your display name, and give yourself or a group (owner or admin) a picture. The browser crops and shrinks the picture before it is uploaded straight to object storage; the server checks what was stored, serves it with a one-year cache (a changed picture gets a new address), removes the old file, and tells everyone who shares a conversation | Implemented |
| Pushes that survive an outage: a message, a notification, a new conversation and a read position are written to an outbox table in the same transaction as the change, and a background relay on every server delivers any push that failed, for example while Redis was down | Implemented |
| File attachments: pictures, videos and documents sent straight to object storage, shown in the conversation | Implemented |
| Notifications: a bell with an unread count and a panel, @mentions (with autocomplete and highlighting), being added to a group, browser alerts, an unread count in the tab title, and muting a conversation | Implemented |
| Structured logging (JSON in production) and tracing (OpenTelemetry, viewed in Jaeger) | Implemented |
| Deployment: Docker images for the API, a migration job and the web server, one Compose file for a single machine with Caddy (automatic HTTPS) in front of two API instances, Postgres and Redis | Implemented (run on one machine; not deployed to a server) |

## How it is built

- **Vertical slices.** Each feature owns its endpoint, MediatR handler, validator and data access in one folder, instead of spreading across technical layers.
- **REST writes, SignalR pushes.** Clients send messages with a REST call; the server saves the message, then pushes it to the conversation's SignalR group. The same save stores an outbox row for the push, so a push that fails is retried by a relay instead of forgotten. Loading data and confirming changes are REST; live updates are SignalR.
- **One group per conversation.** A connection joins the group of every conversation its user belongs to, so the sidebar and the open chat both update live. Redis fans events out across server instances.
- **Predictable errors.** FluentValidation runs in the MediatR pipeline, domain failures use a small exception hierarchy, and every API error, including bare 401 and 404 responses, is an RFC 7807 `ProblemDetails` response.
- **Hardened login.** A per-address rate limit and a per-account lockout (five failures, 15 minutes) slow password guessing, and the JWT settings are validated when the app starts so a weak or missing signing key stops it immediately.
- **Safe edges.** Every response carries browser security headers and API responses are not cacheable, HSTS is sent in production, request bodies are capped at 1 MB, tokens stop working when they expire (including on open SignalR connections), and registration is race-safe: twelve simultaneous sign-ups with one email create one account.
- **Honest health check.** `/health` asks Postgres and Redis and answers 503 when either is down, without revealing why.
- **Query cost is tested, not assumed.** The conversation list and message history have tests that ask Postgres how many rows the real query read, so a change that makes either scan far more data than it returns fails the build.
- **Sessions you can end.** A signed access token cannot be taken back, so it lives 15 minutes and only in memory. The long-lived refresh token is an httpOnly cookie that scripts cannot read, stored on the server as a hash, and replaced on every use. If a replaced token turns up again, someone holds a copy, and the whole session is revoked. Two tabs refreshing at once is told apart from theft by timing, and exactly one of them wins.
- **Presence that survives several servers.** Who is online lives in Redis and changes only through Lua scripts, so two servers cannot interleave their steps. Every event carries Redis's own clock, which settles which of two reports is newer. Each server renews the connections it holds, so browsers send no heartbeats, and a server that dies is noticed when its entries expire. A short wait before "offline" keeps a page refresh from flashing the person offline, and only the contacts of a user are told anything.
- **Files that never pass through the API.** To attach a file the browser first asks the API for permission and gets a presigned link to the bucket, then sends the file there directly. When the message is sent the API looks at what was really stored (a presigned upload cannot cap or inspect it), copies each file inside the bucket only if it is still the object it checked, and saves the message with its files in one transaction. Links to files are signed for each reader and expire; a client asks for a new one.
- **Duplicate-proof direct chats.** A sorted `DirectKey` with a unique partial index in Postgres guarantees at most one conversation per pair of users.
- **Changing a sent message is one statement.** An edit or a delete is a single conditional `UPDATE` (not deleted, still inside the edit window), so an edit racing a delete is settled by Postgres and not by application code. Every copy of a message carries an edit count and a deleted flag, and the browser keeps the later copy whichever way it arrives, so a late or repeated push cannot bring back text that was edited away or a message that was deleted.
- **One owner, even when requests arrive together.** Every change to a group's members runs behind a per-group advisory lock in Postgres, with the rules applied to the rows read after the lock was taken, so an owner handing the group over while removing the person it goes to cannot leave a group with no owner. The lock is covered by tests that fail without it.
- **A removed person stops hearing the group everywhere.** A SignalR connection joins a group once, so removal also takes the person's connections out of it: each server instance keeps its own connections by user, and a message on a Redis channel tells every instance to act on the ones it holds. It is part of the same outbox job as the push, so a failed publish is retried.
- **A retried send is stored once.** The id the browser invents for a message is stored with it, under a partial unique index on conversation, sender and id. A repeat of a send is answered with the stored message (200 instead of 201), and two copies arriving at once are settled by the index, so the loser answers with the winner's message instead of failing. A browser that lost the answer finds its own message in the history by that id and drops the failed copy.
- **A browser store that merges, not overwrites.** Messages reach the screen from REST responses and from SignalR pushes, which race and sometimes deliver the same message twice. Both feed one store through actions that merge by id, so order does not matter. Your own message is shown at once as "sending" and confirmed by whichever arrives first, matched by an id the browser invents.
- **Logic you can test without a browser.** The hub wiring, ordering, reconciliation and typing timers are plain TypeScript tested in Node with a fake hub and fake timers; the tests were checked by breaking each rule on purpose.

## Tech stack

| Layer | Technology |
|---|---|
| Frontend | React 19, TypeScript, Vite, Tailwind CSS 4, Zustand, TanStack Query, React Router, `@microsoft/signalr` |
| Backend | ASP.NET Core Minimal API and SignalR, MediatR, FluentValidation, EF Core |
| Database | PostgreSQL 16 |
| Real-time and cache | SignalR with a Redis 7 backplane |
| Auth | ASP.NET Core Identity, JWT bearer |
| Observability | Serilog (structured logs), OpenTelemetry traces viewed in Jaeger |
| File storage | Any S3-compatible bucket through the S3 API: Cloudflare R2 in production, RustFS locally |
| Local infrastructure | Docker Compose (Postgres, Redis, RustFS, Jaeger, Adminer) |
| Deployment | Docker images, Caddy (automatic HTTPS, reverse proxy), Docker Compose on one machine |

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A current [Node.js](https://nodejs.org) LTS
- [Docker](https://www.docker.com/products/docker-desktop)
- The EF Core CLI: `dotnet tool install --global dotnet-ef`

### 1. Start the infrastructure

```bash
cp .env.example .env        # then set a strong POSTGRES_PASSWORD in .env
docker-compose up -d
```

This starts Postgres (5432), Redis (6379), RustFS, an S3-compatible object store for attachments (9000, console on 9001), Jaeger (16686) and Adminer (8080), and runs a one-off job that creates the storage bucket with its CORS rules. Put `RUSTFS_ACCESS_KEY` and `RUSTFS_SECRET_KEY` (any values you like) and `STORAGE_BUCKET=chatnet` in `.env` first; `.env.example` lists them.

### 2. Configure and run the backend

Secrets are stored with `dotnet user-secrets`, never in files that are committed.

```bash
cd backend/ChatNET.API

dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=chatnet;Username=chatnet_user;Password=<POSTGRES_PASSWORD from .env>"
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 32)"
dotnet user-secrets set "Storage:ServiceUrl" "http://localhost:9000"
dotnet user-secrets set "Storage:Bucket" "<STORAGE_BUCKET from .env>"
dotnet user-secrets set "Storage:AccessKey" "<RUSTFS_ACCESS_KEY from .env>"
dotnet user-secrets set "Storage:SecretKey" "<RUSTFS_SECRET_KEY from .env>"

dotnet ef database update   # creates the schema
dotnet run                  # http://localhost:5045
```

In Development the OpenAPI document is served at `/openapi/v1.json` and the Scalar API reference at `/scalar/v1`; every endpoint documents its success body and its errors.

To see traces, set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` before `dotnet run` and open Jaeger at http://localhost:16686. With the variable unset nothing is exported. Logs go to the console: readable lines in Development, one JSON object per line in any other environment.

### 3. Run the frontend

```bash
cd frontend
npm install
npm run dev                 # http://localhost:5173
```

Vite proxies `/api` and `/hubs` to the backend, so the browser only ever talks to port 5173.

To try the app as two users at once, open `http://localhost:5173` for one and `http://[::1]:5173` (or `http://127.0.0.1:5173` if your Vite server answers there) for the other; the sign-in cookie is kept per host, so two tabs on one host share a session, and a different host (`localhost`, `[::1]`, `127.0.0.1`) is a separate browser as far as the app is concerned.

## Deploying to a server

`docker-compose.prod.yml` describes the whole application for one machine, for example a small VPS:

- **Caddy** serves the built frontend, passes `/api`, `/hubs` and `/health` to the API, and gets and renews the HTTPS certificate. It is the only thing reachable from outside (ports 80 and 443). The page and the API share one address, so the sign-in cookie stays first-party and no CORS is needed.
- **api**, two instances by default. They share the work and reach each other through Redis, so a message sent through one reaches a browser connected to the other, and Caddy keeps each browser on one instance (SignalR needs that).
- **migrate**, a job that applies the database migrations and exits before the API instances start, so several instances starting together never change the schema at once.
- **postgres** and **redis**, with passwords, data in volumes, and no published ports.
- **jaeger**, only with `--profile tracing`; set `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` in `.env.prod` to send the API's traces to it. Its page listens on the machine's own loopback address only, so reach it through an ssh tunnel (`ssh -L 16686:localhost:16686 <server>`).

Files are kept in object storage that is not part of this stack: Cloudflare R2 (or any S3-compatible service). Create the bucket and apply the CORS and lifecycle rules in `docker/storage` (`cors.json` needs your own address in `AllowedOrigins`).

### What you need

- A Linux machine with Docker and the Compose plugin, and ports 80 and 443 open to the internet. It can be x86-64 or ARM64: the images are built for the CPU of the machine that builds them, so the commands below are the same on both.
- A domain name whose DNS records point at that machine. Caddy asks for the certificate as soon as it starts, so the records must be right before the first start.
- An R2 bucket and an access key for it.

### First start

```bash
git clone <this repository> chatnet && cd chatnet
cp .env.prod.example .env.prod      # fill in every value; the file explains each one
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
docker compose -f docker-compose.prod.yml --env-file .env.prod ps
```

The first build takes a few minutes. When `ps` shows the API instances as healthy, open `https://<your domain>`. `docker compose ... logs -f api` follows the API (one JSON object per line).

### Updating

```bash
git pull
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
```

The migration job runs first and does nothing when the database is already up to date. The API instances are replaced together, so there is a short break while they start; this stack does not do rolling updates.

### Backups

The database is the only thing here that cannot be rebuilt. Take a dump on a schedule and copy it off the machine:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod exec -T postgres \
  sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' | gzip > chatnet-$(date +%F).sql.gz
```

Uploaded files are in the bucket, not in the dump. Keep Caddy's `caddy-data` volume if you can: it holds the certificate account, and the certificate authority limits how often new certificates may be requested.

### Trying it on one machine

Set `DOMAIN=localhost` in `.env.prod`. Caddy then uses its own certificate authority, which a browser does not trust, so the browser warns once; `curl -k https://localhost/health` shows the stack answering. To skip TLS altogether, also set `SITE_ADDRESS=http://localhost`. The development setup in `docker-compose.yml` and this stack use different project names, so they never share volumes.

### Limits

- The login and refresh rate limits are counted in each API instance, so with two instances the effective allowance per client is up to twice what is configured.
- One Postgres and one Redis, with no replication and no automatic backups.
- Nothing here has run on a real server or against Cloudflare R2; it has been run on one machine with the object store left unconfigured. The ARM64 images have been built and checked, and the whole stack has run as ARM64 only under emulation on an x86-64 machine, not on a real ARM server.

## Tests

Backend tests run the real API in-process against a real Postgres and a real Redis, each started in a throwaway Docker container, so Docker must be running. They never touch the development database and need no secrets or configuration.

```bash
dotnet test        # backend, from the repository root
cd frontend && npm test    # frontend logic tests (Vitest, no browser needed)
```

GitHub Actions runs both on every push and pull request, along with the frontend lint, build and a dependency vulnerability check for both the backend and the frontend.

## Project layout

```
backend/ChatNET.API/
  Auth/            registration, login, JWT
  Users/           user search
  Conversations/   direct and group conversations, membership
  Messaging/       messages and the SignalR hub
  Media/           file uploads and attachments: object storage, upload tickets, links
  Notifications/   mentions and group invitations as notifications, the list behind the bell
  Common/          shared behaviours, exceptions, persistence, hosting (trusted proxies)
backend/ChatNET.API.Tests/
  Infrastructure/  test host (WebApplicationFactory + Testcontainers for Postgres, Redis and the object store), helpers
frontend/src/
  features/        auth, chat, notifications, presence (one folder per feature)
  components/ui/   shared UI primitives
  lib/             API client, query client
deploy/            Caddyfile (the proxy in front of the production stack)
backend/Dockerfile, frontend/Dockerfile, docker-compose.prod.yml, .env.prod.example   the production stack
```
