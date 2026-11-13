# Features

One page per feature: what it does, the endpoints and hub events it uses, and its limits.

| Feature | What it covers |
|---|---|
| [Accounts and sessions](accounts-and-sessions.md) | Registration, sign-in, short-lived access tokens, rotating refresh tokens, sign-out |
| [Account recovery](account-recovery.md) | Forgot and reset password by email, single-use links, every session ended |
| [Conversations](conversations.md) | Direct and group conversations, the list, user search |
| [Messaging and history](messaging.md) | Sending (idempotent), history paging, catch-up after a reconnect, optimistic sending |
| [Searching messages](message-search.md) | Search in the browser over a local copy, the syntax, the changes feed that keeps the copy current |
| [Editing and deleting](edit-and-delete.md) | The edit window, who can delete, live updates |
| [Read state](read-state.md) | Read position, unread counts, "Seen" |
| [Presence and typing](presence-and-typing.md) | Online dot, last seen, typing indicators |
| [Attachments](attachments.md) | Files encrypted in the browser, uploaded straight to storage, checked for size at send, signed links |
| [Notifications and mentions](notifications.md) | The bell, `@mentions`, mute, browser alerts |
| [Group management](group-management.md) | Roles, add, remove, hand over, rename, leave |
| [Profiles and pictures](profiles-and-pictures.md) | Display name, pictures for people and groups |
| [Rate limits](rate-limits.md) | Limits per address and per person, shared across instances |
| [Web Push](web-push.md) | Alerts while the app is closed |
| [Encryption keys and devices](encryption-keys.md) | Keys made in the browser, the passphrase backup, linking a new device, signing out |
| [Conversation keys](conversation-keys.md) | The key of each period of a conversation, sealed for each member; what the server and the browser check |

For the contract these features use, see the [API reference](../api/endpoints.md). For how they are built, see the [architecture pages](../architecture/overview.md).
