# Group management

Add people, remove someone, change roles, hand the group over, rename and leave.

## Roles

| Role | Can |
|---|---|
| Owner (one per group) | everything below, and change anyone's role, including handing the group over |
| Admin | add people, remove plain members, rename the group, change the group picture, delete anyone's message |
| Member | send, read, and leave |

A direct chat has no admins and none of these changes apply to it (409).

## Operations

| Action | Endpoint | Rule |
|---|---|---|
| Add people | `POST /api/conversations/{id}/members` `{ userIds, epoch }` | 1 to 100 ids, and the new key epoch made for everyone who will be in the group (see [conversation keys](conversation-keys.md)). Owner or admin. They join as plain members. People already in are skipped; someone who left comes back with a new join time. A group holds its owner and 100 others at most. Added people get a `MemberAdded` notification. |
| Remove someone | `DELETE /api/conversations/{id}/members/{userId}` | The owner removes anyone; an admin removes plain members only. Removing oneself is a 409 (leave instead). The epoch in force is marked due for replacement. |
| Change role | `PUT /api/conversations/{id}/members/{userId}/role` `{ role }` | Owner only. `Owner` hands the group over and makes the old owner an admin. One's own role cannot be changed. |
| Leave | `POST /api/conversations/{id}/leave` | Answers `{ leftAt }`. When the owner leaves, the group goes to the admin (otherwise the member) who has been in the longest, in the same transaction. The epoch in force is marked due for replacement. Leaving twice is a 403. |
| Rename | `PATCH /api/conversations/{id}` `{ name }` | Owner or admin. 1 to 100 characters. The conversation does not move in anyone's list. |
| Group picture | see [profiles and pictures](profiles-and-pictures.md) | Owner or admin. |

The list-changing calls answer with the group's members after the change.

## Order of checks

In the conversation (403, the same answer whether or not it exists), then that it is a group (409), then allowed to do it (403), then the target (404), then the rule (409). An add checks who may add before it looks up which accounts exist, so a plain member learns nothing about the user base.

## Concurrency

Every change runs in a transaction behind a **per-conversation advisory lock**, with the rules applied to the rows read after the lock was taken. An owner handing the group over while removing the person it goes to therefore cannot leave a group with no owner or two owners.

## After someone stops being a member

- They keep the conversation, read-only, with history bounded to the time they were a member (`JoinedAt` to `LeftAt`). They cannot send, list the people, or fetch files from after they left.
- Every open connection of theirs, on every API instance, is taken out of the conversation's SignalR group and the typing check. See [real-time design](../architecture/real-time.md).
- Their tabs are told with `RemovedFromConversation`.

## Pushes

`MembersChanged { conversationId, members, removedUserIds }` goes to each person who is in the group now, one by one, not to the group of connections, so someone just removed never hears who is left. It carries the entries as they are when it is sent, so applying it twice or late ends in the same list. `ConversationUpdated { id, name, avatarUrl }` follows a rename or a picture change. `AddedToConversation` goes to people added, and `RemovedFromConversation` to the person removed. `ConversationKeyChanged` goes to the people who are in the group now after an add (the new epoch), a removal and a leave (the epoch is due for replacement).

## In the browser

A Group info dialog holds rename, the people with a menu per person, an add-people picker, leave, and confirmation panels. A group you left shows a "Left" chip and a notice in place of the composer.

## Limits

A person who left can show a stale presence dot for former co-members until a reload.
