using ChatNET.API.Conversations.Membership;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Conversations.Persistence;

// One change to a conversation's membership, made while holding that conversation's lock: anyone else who wants to
// change the same conversation waits until this one is committed or disposed. That makes the rows in Members the
// current rows for as long as the change lasts, so a rule such as "an admin cannot remove an admin" is checked against
// the state it will be saved into, however many requests arrive together.
//
// Disposing without committing discards the change and frees the lock, which is what a refused call does.
public interface IMembershipChange : IAsyncDisposable
{
    // The conversation as it is now, tracked, so a rename is a change to this object.
    Conversation Conversation { get; }

    // The rules, applied to every row of the conversation (the ones who left included).
    GroupMembers Members { get; }

    // The first check of every change: the person is in the conversation (anyone else gets the refusal every endpoint
    // gives, which says nothing about whether the conversation exists), and it is a group. A direct chat has no members
    // to manage.
    void RequireGroupMember(Guid userId);

    // Saves what was changed together with the notifications and outbox rows that belong to it, in one transaction, and
    // frees the lock.
    Task CommitAsync(
        IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken);
}
