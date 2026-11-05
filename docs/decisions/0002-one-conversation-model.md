# One conversation model for direct and group chats

Status: Accepted

## Context

Messaging, read state, presence and notifications behave the same in a 1:1 chat and a group. Separate models would duplicate each of them.

## Decision

`Conversations` holds both kinds, with `Type` of `Direct` or `Group`. `ConversationMembers` holds who is in each, with a role, `JoinedAt`, `LeftAt`, a read position and a mute flag. Everything else works against a conversation id and never branches on the kind. A member who leaves keeps their row with `LeftAt` set instead of an active flag, so history can be bounded to the time they were a member.

## Alternatives considered

- Separate tables for direct and group conversations: every feature would need two code paths.

## Consequences

- A direct chat has the same membership rows as a group, with no admin operations allowed on it (they answer 409).
- A conversation name is resolved per viewer: a direct chat shows the other person's name.

## Related

- [Data model](../architecture/data-model.md)
- [Conversations](../features/conversations.md)
