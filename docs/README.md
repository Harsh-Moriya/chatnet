# ChatNET documentation

ChatNET is a real-time chat application with direct and group conversations. This folder is the reference for how it works and how to run it. The [root README](../README.md) is the short introduction and the quick start.

## Reading order

| If you want to | Read |
|---|---|
| Understand the system | [Architecture overview](architecture/overview.md), then [request flow](architecture/request-flow.md) and [real-time design](architecture/real-time.md) |
| See what is stored | [Data model](architecture/data-model.md) |
| Know how it is protected | [Security model](architecture/security-model.md) |
| Understand how messages are encrypted | [Encryption keys and devices](features/encryption-keys.md), [conversation keys](features/conversation-keys.md), [safety numbers](features/safety-numbers.md) |
| Implement or review the encryption formats | [Encryption formats](architecture/encryption-format.md) |
| See what the app does | [Features](features/README.md) |
| Build a client against the API | [Endpoints](api/endpoints.md), [errors](api/errors.md), [real-time events](api/realtime-events.md) |
| Run or deploy it | [Running locally](operations/running-locally.md), [configuration](operations/configuration.md), [deployment](operations/deployment.md) |
| Operate it | [Backups](operations/backups.md), [observability](operations/observability.md) |
| Learn why it is built this way | [Decision records](decisions/README.md) |

## Sections

- **architecture/**: how the pieces fit together, with diagrams.
- **features/**: one page per feature, with behaviour, endpoints, events and limits.
- **api/**: the HTTP and SignalR contract.
- **operations/**: running, configuring, deploying and watching the system.
- **decisions/**: architecture decision records.

## Conventions used in these pages

- Identifiers are UUIDs and timestamps are ISO 8601 in UTC.
- A "member" means an active member of a conversation. Someone who left a group keeps read-only access to what was said while they were a member.
- Limits and defaults quoted here are the defaults in `appsettings.json` and the options classes; [configuration](operations/configuration.md) lists how to change each.
