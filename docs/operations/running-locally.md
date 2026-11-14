# Running locally

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download). The production image pins the ASP.NET runtime at 10.0.13; use 10.0.13 or later wherever the API runs.
- A current [Node.js](https://nodejs.org) LTS.
- [Docker](https://www.docker.com/products/docker-desktop), for the infrastructure and for the backend tests.
- The EF Core CLI: `dotnet tool install --global dotnet-ef`.

## 1. Start the infrastructure

```bash
cp .env.example .env
```

Edit `.env`: set a strong `POSTGRES_PASSWORD`, and choose `RUSTFS_ACCESS_KEY` and `RUSTFS_SECRET_KEY` (any values) and `STORAGE_BUCKET=chatnet`. Then:

```bash
docker-compose up -d
```

| Service | Port | Purpose |
|---|---|---|
| Postgres 16 | 5432 | the database |
| Redis 7 | 6379 | backplane, presence, rate limits |
| RustFS | 9000 (S3 API), 9001 (console) | S3-compatible object storage for attachments and pictures |
| `storage-init` | none | a one-off job that creates the bucket and applies its CORS and lifecycle rules |
| Jaeger | 16686 (UI), 4317 and 4318 (OTLP) | trace viewer |
| Adminer | 8081 | a database browser |
| Mailpit | 1025 (SMTP), 8025 (web page) | a mail catcher: it accepts any message and shows it at `http://localhost:8025`, so password reset emails can be read without a mail provider. Nothing is delivered anywhere, and the mail is gone when the container stops. |

## 2. Configure and run the backend

Secrets live in `dotnet user-secrets`, never in files that are committed.

```bash
cd backend/ChatNET.API

dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=chatnet;Username=chatnet_user;Password=<POSTGRES_PASSWORD from .env>"
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 32)"
dotnet user-secrets set "Storage:ServiceUrl" "http://localhost:9000"
dotnet user-secrets set "Storage:Bucket" "<STORAGE_BUCKET from .env>"
dotnet user-secrets set "Storage:AccessKey" "<RUSTFS_ACCESS_KEY from .env>"
dotnet user-secrets set "Storage:SecretKey" "<RUSTFS_SECRET_KEY from .env>"
```

Create the schema and start the API:

```bash
dotnet ef database update
```

```bash
dotnet run
```

The API listens on `http://localhost:5045`. In Development it serves its OpenAPI document at `/openapi/v1.json` and the Scalar API reference at `/scalar/v1`. Check it with:

```bash
curl http://localhost:5045/health
```

which answers `{"status":"healthy","checks":{"postgres":"healthy","redis":"healthy"}}` when Postgres and Redis are reachable.

### Password reset emails (optional)

Without a mail relay the app works as before, and asking for a password reset link answers that it is not available. To try it, point the API at Mailpit:

```bash
dotnet user-secrets set "Email:Smtp:Host" "localhost"
dotnet user-secrets set "Email:Smtp:Port" "1025"
dotnet user-secrets set "Email:Smtp:Security" "None"
dotnet user-secrets set "Email:From:Address" "noreply@chatnet.local"
```

Restart the API, choose "Forgot password?" on the sign-in page, and open `http://localhost:8025` for the email. The link in it opens the reset page. The mail is sent by the outbox relay, which runs every 2 seconds, so it arrives a moment after the request. Asking is limited to 5 requests per 15 minutes per address and 3 emails an hour per email address; see [rate limits](../features/rate-limits.md).

### Alerts while the app is closed (optional)

Without these settings the app works as before and does not offer push alerts. Make a key pair once and set three values; the profile then gets a switch (a browser needs a secure page, and `localhost` counts):

```bash
node -e "const c=require('crypto');const e=c.createECDH('prime256v1');e.generateKeys();console.log('Push:PublicKey  ',e.getPublicKey('base64url'));console.log('Push:PrivateKey ',Buffer.from(e.getPrivateKey('hex').padStart(64,'0'),'hex').toString('base64url'))"
```

```bash
dotnet user-secrets set "Push:PublicKey" "<the first value>"
dotnet user-secrets set "Push:PrivateKey" "<the second value>"
dotnet user-secrets set "Push:Subject" "mailto:you@example.com"
```

### Traces and logs

To see traces, set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` before `dotnet run` and open Jaeger at `http://localhost:16686`. With the variable unset nothing is exported. Logs go to the console: readable lines in Development, one JSON object per line in any other environment. See [observability](observability.md).

## 3. Run the frontend

```bash
cd frontend
npm install
```

```bash
npm run dev
```

The app is at `http://localhost:5173`. Vite proxies `/api` and `/hubs` (with WebSocket upgrade) to the backend, so the browser only ever talks to port 5173.

### Trying the app as two users

The refresh cookie is kept per host, so two tabs on one host share a session. To be two people at once, open `http://localhost:5173` for one and `http://127.0.0.1:5173` (or `http://[::1]:5173`) for the other; each host name is a separate browser as far as the app is concerned. Each person also sets up their encryption keys the first time they sign in (a recovery passphrase of at least 12 characters), and two people can only chat once both have. The bucket's CORS rule in `docker/storage/cors.json` lists these three origins.

## Tests

Backend tests run the real API in-process against a real Postgres, a real Redis and a real object store, each started in a throwaway container, so Docker must be running. They never touch the development database and need no secrets or configuration.

```bash
dotnet test
```

```bash
cd frontend && npm test
```

Other frontend checks: `npm run lint` and `npm run build` (which also type-checks with `tsc -b`).

## Resetting

`docker-compose down` stops the services and keeps the data volumes; add `-v` to delete the data too. After Docker Desktop restarts, the containers are stopped: run `docker-compose up -d` again, and `dotnet ef database update` if new migrations arrived.
