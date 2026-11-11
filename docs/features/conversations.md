# Conversations

Direct (1:1) and group conversations share one model. A conversation is a row; who is in it is a set of membership rows.

## Behaviour

- **Direct chats are find-or-create.** `POST /api/conversations/direct` with the other person's id (and the `id` and first `epoch` of a new chat, see [conversation keys](conversation-keys.md)) returns the existing chat if there is one and creates it otherwise, so the call is safe to repeat. A unique partial index on `DirectKey` (the two user ids sorted and joined) guarantees at most one direct chat per pair, even when both people start one at the same moment.
- **Groups** are created with an `id`, a name (1 to 100 characters), members and the first `epoch` (see [conversation keys](conversation-keys.md)). Everyone named must have set up encryption. The creator is the owner. A group holds its owner and up to 100 others. Creating a group notifies each member except the creator.
- **The list.** `GET /api/conversations` returns every conversation the caller is in, newest activity first. A group the caller left stays in the list, read-only. Each entry carries the last message (with `attachmentCount`), the caller's `unreadCount` (capped at 100, shown as "99+"), `isMuted`, `leftAt`, `keyEpoch` and `keyRotationRequired` (see [conversation keys](conversation-keys.md)), and for a direct chat the other person's id (`otherUserId`).
- **One conversation.** `GET /api/conversations/{id}` returns the same object as a list entry.
- **Live creation.** When a conversation is created, its members are told with `AddedToConversation` (the creator's other tabs too), and each client then calls `JoinConversation` so the connection joins the group. Only the request that created a direct chat announces it.
- **User search.** `GET /api/users?query=` returns up to 20 people whose handle or display name starts with the query (at least 2 characters), never the caller. It uses trigram indexes (`pg_trgm`) on the handle and the display name instead of reading every user, and the text searched for is never written to the log.
- **Mute.** `PUT /api/conversations/{id}/mute` sets the caller's own choice. See [notifications](notifications.md).

## Access rules

- Every conversation endpoint checks that the caller is an active member. A conversation that does not exist and one the caller is not in give the identical 403.
- Someone who left a group still gets the conversation, with `leftAt` set and nothing unread, and its preview is the last message from before they left. They cannot send and they cannot list the people.

## Endpoints

`GET /api/conversations`, `GET /api/conversations/{id}`, `POST /api/conversations/direct`, `POST /api/conversations/group`, `GET /api/users`. Group changes are on the [group management](group-management.md) page. See [endpoints](../api/endpoints.md).

## Performance

The list is one hand-written SQL query with a `LEFT JOIN LATERAL` for each conversation's latest message, so its cost depends on the caller's own conversations, not the total number of messages. A test asks Postgres how many `Messages` rows the real query reads. The unread count is a subquery over the same index that stops after 100 rows.

## Limits

- The list is not paginated.
- A direct chat has no admins and cannot be renamed, left or have members changed.
