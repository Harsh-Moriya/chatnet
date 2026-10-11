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
| Loading older messages with cursor pagination | Planned |
| Online presence, read receipts, offline delivery | Planned |
| Refresh tokens, notifications, file uploads | Planned |
| Structured logging, tracing, deployment | Planned |

## How it is built

- **Vertical slices.** Each feature owns its endpoint, MediatR handler, validator and data access in one folder, instead of spreading across technical layers.
- **REST writes, SignalR pushes.** Clients send messages with a REST call; the server saves the message, then pushes it to the conversation's SignalR group. Loading data and confirming changes are REST; live updates are SignalR.
- **One group per conversation.** A connection joins the group of every conversation its user belongs to, so the sidebar and the open chat both update live. Redis fans events out across server instances.
- **Predictable errors.** FluentValidation runs in the MediatR pipeline, domain failures use a small exception hierarchy, and every API error, including bare 401 and 404 responses, is an RFC 7807 `ProblemDetails` response.
- **Hardened login.** A per-address rate limit and a per-account lockout (five failures, 15 minutes) slow password guessing, and the JWT settings are validated when the app starts so a weak or missing signing key stops it immediately.
- **Query cost is tested, not assumed.** The conversation list and message history have tests that ask Postgres how many rows the real query read, so a change that makes either scan far more data than it returns fails the build.
- **Duplicate-proof direct chats.** A sorted `DirectKey` with a unique partial index in Postgres guarantees at most one conversation per pair of users.
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
| Local infrastructure | Docker Compose (Postgres, Redis, Jaeger, Adminer) |

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

This starts Postgres (5432), Redis (6379), Jaeger (16686) and Adminer (8080).

### 2. Configure and run the backend

Secrets are stored with `dotnet user-secrets`, never in files that are committed.

```bash
cd backend/ChatNET.API

dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=chatnet;Username=chatnet_user;Password=<POSTGRES_PASSWORD from .env>"
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 32)"

dotnet ef database update   # creates the schema
dotnet run                  # http://localhost:5045
```

In Development the OpenAPI document is served at `/openapi/v1.json`.

### 3. Run the frontend

```bash
cd frontend
npm install
npm run dev                 # http://localhost:5173
```

Vite proxies `/api` and `/hubs` to the backend, so the browser only ever talks to port 5173.

To try the app as two users at once, open `http://localhost:5173` for one and `http://[::1]:5173` (or `http://127.0.0.1:5173` if your Vite server answers there) for the other; browser storage is separate per origin.

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
  Common/          shared behaviours, exceptions, persistence
backend/ChatNET.API.Tests/
  Infrastructure/  test host (WebApplicationFactory + Testcontainers), helpers
frontend/src/
  features/        auth, chat (one folder per feature)
  components/ui/   shared UI primitives
  lib/             API client, query client
```
