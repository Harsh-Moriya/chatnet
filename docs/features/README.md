# Features

One page per feature: what it does, the endpoints and hub events it uses, and its limits.

| Feature | What it covers |
|---|---|
| [Accounts and sessions](accounts-and-sessions.md) | Registration, sign-in, short-lived access tokens, rotating refresh tokens, sign-out |
| [Conversations](conversations.md) | Direct and group conversations, the list, user search |
| [Messaging and history](messaging.md) | Sending (idempotent), history paging, catch-up after a reconnect, optimistic sending |
| [Editing and deleting](edit-and-delete.md) | The edit window, who can delete, live updates |
| [Read state](read-state.md) | Read position, unread counts, "Seen" |
| [Presence and typing](presence-and-typing.md) | Online dot, last seen, typing indicators |
| [Attachments](attachments.md) | Direct-to-storage uploads, checked at send, signed links |
| [Notifications and mentions](notifications.md) | The bell, `@mentions`, mute, browser alerts |
| [Group management](group-management.md) | Roles, add, remove, hand over, rename, leave |
| [Profiles and pictures](profiles-and-pictures.md) | Display name, pictures for people and groups |
| [Message search](search.md) | Full-text search over what the person may read |
| [Rate limits](rate-limits.md) | Limits per address and per person, shared across instances |
| [Web Push](web-push.md) | Alerts while the app is closed |

For the contract these features use, see the [API reference](../api/endpoints.md). For how they are built, see the [architecture pages](../architecture/overview.md).
