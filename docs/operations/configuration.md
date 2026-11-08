# Configuration

## How configuration is read

Layers, later ones overriding earlier ones: `appsettings.json`, `appsettings.{Environment}.json` (not tracked by git), user-secrets (Development only), then environment variables. Nested keys are written with `:` in files and `__` in environment variables (`Storage:Bucket` is `Storage__Bucket`).

Options are bound to classes and **validated when the host starts**, so a missing or invalid value stops the API with a message naming the key, instead of failing on first use. Never put a secret in a committed file: use `dotnet user-secrets` locally and environment variables elsewhere.

## Connections and secrets

| Key | Purpose | Notes |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | PostgreSQL | A secret. Not validated at start; fails on first use. |
| `ConnectionStrings:Redis` | SignalR backplane, presence, rate limits | A secret when it carries a password. The API does not start without it. |
| `Jwt:SigningKey` | Signs access tokens | A secret. At least 32 bytes. Changing it signs everyone out. |
| `Storage:AccessKey`, `Storage:SecretKey` | Credentials of the bucket | Secrets; both required; never printed. |
| `Push:PublicKey`, `Push:PrivateKey`, `Push:Subject` | Identify the server to push services | See [Web Push](#web-push). The private key is a secret. |
| `Email:Smtp:UserName`, `Email:Smtp:Password` | The login of the mail relay | See [Email](#email). The password is a secret. |

## Tokens

| Key | Default | Validation |
|---|---|---|
| `Jwt:Issuer`, `Jwt:Audience` | `chatnet-api`, `chatnet-client` | required |
| `Jwt:AccessTokenMinutes` | 15 | above zero |
| `Jwt:RefreshTokenDays` | 14 (counted from the last refresh) | above zero |
| `Jwt:RefreshReuseGraceSeconds` | 30 (how long after a refresh token was replaced it may be presented again without counting as theft) | not negative |

## Web and hosting

| Key | Default | Purpose |
|---|---|---|
| `Frontend:Url` | `http://localhost:5173` | The one origin CORS allows (with credentials), and the address the links in emails point at. When email is on it must be the address of the web app: absolute, `https` (plain `http` only for localhost), with no path, query or fragment. |
| `AllowedHosts` | `*` | Host names the API answers, separated by semicolons; any other `Host` gets a 400. |
| `ForwardedHeaders:KnownNetworks` | empty | Networks (CIDR, for example `172.16.0.0/12`) the reverse proxy connects from. Empty means no proxy and forwarded headers are ignored. Each entry must parse. In an environment variable: `ForwardedHeaders__KnownNetworks__0`, `__1`, and so on. |

## Object storage

| Key | Default | Validation |
|---|---|---|
| `Storage:ServiceUrl` | none | An absolute http or https address. It is signed into links, so browsers must reach the same address as the server (R2: `https://<account id>.r2.cloudflarestorage.com`; locally `http://localhost:9000`). |
| `Storage:Bucket` | none | 3 to 63 lowercase letters, digits or hyphens |
| `Storage:Region` | `auto` | must be set (Cloudflare R2 takes `auto`) |
| `Storage:MaxFileBytes` | 10485760 (10 MB) | above zero |
| `Storage:MaxFilesPerMessage` | 4 | 1 to 20 |
| `Storage:AllowedContentTypes` | JPEG, PNG, GIF, WebP, MP4, WebM, PDF, plain text, ZIP | at least one, each `type/subtype` |
| `Storage:UploadUrlMinutes` | 10 | above zero |
| `Storage:DownloadUrlMinutes` | 60 | above zero |

## Messaging, presence and the outbox

| Key | Default | Validation |
|---|---|---|
| `Messaging:EditWindow` | 15 minutes | above zero. How long after sending its sender may edit. Deleting has no limit. |
| `Presence:RefreshInterval` | 30 s | above zero. How often each instance renews its connections and sweeps expired users. |
| `Presence:Ttl` | 90 s | at least twice the refresh interval. How long an entry counts as alive without renewal. |
| `Presence:OfflineGrace` | 5 s | not negative, not longer than the TTL. Zero means no wait. |
| `Outbox:RelayInterval` | 2 s | above zero |
| `Outbox:ClaimAfter` | 5 s | not negative. How long a new row is left to its request before the relay may take it. |
| `Outbox:RetryDelay` | 5 s | above zero. Doubles per attempt, up to 64 times the base. |
| `Outbox:BatchSize` | 100 | at least 1 |
| `Outbox:MaxAttempts` | 10 | at least 1 |
| `Outbox:Retention` | 1 day | above zero. How old a row must be before the clean-up removes it, whether it was dispatched or given up on after `Outbox:MaxAttempts`. |
| `Outbox:CleanupInterval` | 1 hour | above zero |

Time values are `hh:mm:ss` (or `d.hh:mm:ss`) strings, for example `Messaging__EditWindow=00:15:00`.

## Rate limits

`RateLimiting:<Policy>:PermitLimit` and `RateLimiting:<Policy>:WindowSeconds` for each policy; `PermitLimit` is at least 1 and `WindowSeconds` is 1 to 86,400.

| Policy | Defaults |
|---|---|
| `Auth` | 10 per 60 s |
| `Refresh` | 60 per 60 s |
| `Send` | 30 per 10 s |
| `Uploads` | 60 per 3600 s |
| `Edit` | 20 per 60 s |
| `Search` | 30 per 10 s |
| `Avatars` | 600 per 60 s |
| `Push` | 20 per 60 s |
| `ForgotPassword` | 5 per 900 s |
| `ResetPassword` | 10 per 900 s |
| `PasswordResetEmail` | 3 per 3600 s (counted per email address by the handler that asks for a link, not by the pipeline) |

| Key | Default | Validation |
|---|---|---|
| `RateLimiting:Store:KeyPrefix` | `ratelimit` | letters, digits, `_`, `.`, `-`. In front of every key in Redis, so environments sharing a Redis do not count each other. |
| `RateLimiting:Store:TimeoutMilliseconds` | 200 | 10 to 10,000. How long Redis may take before the limit is skipped for a request. |
| `RateLimiting:Store:PauseSeconds` | 5 | 1 to 600. How long the limits are skipped after a failure. |

See [rate limits](../features/rate-limits.md).

## Email

Password reset links are sent through an SMTP relay. Email is **off** unless `Email:Smtp:Host` is set: the API still starts, and asking for a reset link answers 503. With a host set, `Email:From:Address` is required and the rest is checked when the host starts.

| Key | Default | Validation |
|---|---|---|
| `Email:Smtp:Host` | none | Empty, or only spaces, means email is off. |
| `Email:Smtp:Port` | 587 | 1 to 65535. |
| `Email:Smtp:Security` | `StartTls` | `StartTls` (required, not merely tried), `SslOnConnect` (TLS from the first byte) or `None`. `None` is refused together with a login. |
| `Email:Smtp:UserName`, `Email:Smtp:Password` | none | Both or neither. A secret: set it with user-secrets or the environment. |
| `Email:Smtp:Timeout` | 10 s | Above zero and at most 10 minutes. How long connecting and each exchange may take; the whole send is cut off after three times this. |
| `Email:From:Address` | none | A valid email address, on a domain the relay allows. Required when the host is set. |
| `Email:From:Name` | `ChatNET` | The sender name. |
| `PasswordReset:TokenLifetime` | 30 minutes | At least one minute. How long a reset link works. |
| `PasswordReset:SendWithin` | 10 minutes | Above zero. A request older than this when the relay reaches it is dropped, not sent, because a link that arrives late is of no use. |

See [account recovery](../features/account-recovery.md).

## Web Push

Push is **off** unless the three keys are set. Some set, or a pair that does not match, stops the host at start.

| Key | Default | Notes |
|---|---|---|
| `Push:PublicKey`, `Push:PrivateKey` | none | A P-256 key pair in base64url: 65 and 32 bytes. Make it once and keep it. |
| `Push:Subject` | none | A contact for the push services: `mailto:` and an address, or an https address. |
| `Push:AllowedHosts` | `fcm.googleapis.com`, `updates.push.services.mozilla.com`, `push.apple.com`, `notify.windows.com` | The push services a registration may name; a host matches its subdomains. At least one, each a plain lower-case host name. |
| `Push:MaxSubscriptionsPerUser` | 10 | At least 1. The least recently used is dropped. |
| `Push:SubscriptionRetention` | 60 days | Above zero. |
| `Push:TimeToLive` | 1 day | At least a second. How long a push service holds a push for a browser that is off. |
| `Push:RequestTimeout` | 10 s | Above zero. |
| `Push:MaxParallelSends` | 8 | At least 1. Pushes in flight for one alert. |

## Logging and tracing

| Key | Default | Purpose |
|---|---|---|
| `Serilog:MinimumLevel:Default` | `Information` | Log level. |
| `Serilog:MinimumLevel:Override:<category>` | `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore` and `System.Net.Http.HttpClient` at `Warning` | Level per category. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Where traces are sent over OTLP (the local Jaeger is `http://localhost:4317`). Unset exports nothing. |
| `OTEL_SERVICE_NAME` | `chatnet-api` | The service name on every span. |
| `OTEL_TRACES_SAMPLER`, `OTEL_TRACES_SAMPLER_ARG` | keep all | Standard OpenTelemetry sampling settings. |

## Frontend build

| Variable | Default | Purpose |
|---|---|---|
| `VITE_API_URL` | `/api` | API base URL baked into the build. |
| `VITE_HUB_URL` | `/hubs/chat` | Hub URL baked into the build. |

Picture addresses are relative (`/api/avatars/...`), so the page and the API must share an origin, as they do behind the Vite proxy and the production proxy.

## Docker Compose variables

### Development (`.env`, from `.env.example`)

| Variable | Purpose |
|---|---|
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | The database container |
| `RUSTFS_ACCESS_KEY`, `RUSTFS_SECRET_KEY` | The object-store container's credentials; use the same values for `Storage:AccessKey` and `Storage:SecretKey` |
| `STORAGE_BUCKET` | The bucket the set-up job creates |

### Production (`.env.prod`, from `.env.prod.example`)

Compose refuses a missing required value and names it.

| Variable | Required | Purpose |
|---|---|---|
| `DOMAIN` | yes | The site's host name; its DNS record must point at the machine |
| `SITE_ADDRESS` | no | Caddy's address; set `http://localhost` with `DOMAIN=localhost` to try the stack without TLS |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | yes | The database |
| `REDIS_PASSWORD` | yes | Redis |
| `JWT_SIGNING_KEY` | yes | At least 32 bytes |
| `STORAGE_SERVICE_URL`, `STORAGE_BUCKET`, `STORAGE_ACCESS_KEY`, `STORAGE_SECRET_KEY` | yes | The S3-compatible bucket |
| `PUSH_PUBLIC_KEY`, `PUSH_PRIVATE_KEY`, `PUSH_SUBJECT` | no | Web Push; empty means off |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_SECURITY`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `MAIL_FROM_ADDRESS`, `MAIL_FROM_NAME` | no | The mail relay for password reset emails; an empty `SMTP_HOST` means email is off. The port defaults to 587 and the security to `StartTls`. |
| `ALLOWED_HOSTS` | no | Host names the API answers; default is `DOMAIN` and `localhost` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | Send traces here (with `--profile tracing`, `http://jaeger:4317`) |
| `API_REPLICAS` | no | How many API instances run (default 2) |
| `TRUSTED_PROXY_NETWORK` | no | The network Caddy reaches the API from (default `172.16.0.0/12`) |
| `CHATNET_VERSION` | no | Tags the images built, so an older one can be started again (default `local`) |

Use generated, plain alphanumeric values (for example `openssl rand -hex 32`) for passwords: they are placed in connection strings, where a semicolon or an equals sign would end the value.
