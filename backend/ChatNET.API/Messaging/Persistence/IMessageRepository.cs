using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Messaging.Persistence;

public interface IMessageRepository
{
    // Saves the message, the notifications it causes, the outbox rows for their pushes and its conversation's
    // UpdatedAt (moved to the message's CreatedAt), as one atomic write: either all of it happens or none of it
    // does. Returns false, saving nothing, when the message carries a client id that this sender already used in
    // this conversation: another request sent it first.
    Task<bool> TryAddAsync(
        Message message, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken);

    // The message as it is stored (a deleted one without its content, and without its files), or null. Files are
    // not included: AttachmentLoader adds them.
    Task<MessageDto?> FindByIdAsync(Guid messageId, CancellationToken cancellationToken);

    // The message with what editing or deleting it depends on, or null when the conversation has no such message sent at
    // or after "since". A message from before the caller joined is "no such message", as in history.
    Task<MessageForChange?> FindForChangeAsync(
        Guid conversationId, Guid messageId, DateTime since, CancellationToken cancellationToken);

    // Replaces the text of a message that is not deleted and was sent after notBefore, counts the edit, and saves the
    // notifications and outbox rows that come with it, in one transaction. One conditional UPDATE decides, so an edit
    // that loses to a delete (or arrives after the window closed) changes nothing and returns false.
    Task<bool> TryEditAsync(
        Guid messageId, string content, DateTime editedAt, DateTime notBefore,
        IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken);

    // Marks a message that is not deleted as deleted, removes its text, and saves the outbox rows, in one transaction.
    // False, with nothing saved, when it already was deleted.
    Task<bool> TryDeleteAsync(
        Guid messageId, Guid deletedById, DateTime deletedAt, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken);

    // The message this sender already sent in this conversation with this client id, or null.
    Task<MessageDto?> FindByClientMessageIdAsync(
        Guid conversationId, Guid senderId, Guid clientMessageId, CancellationToken cancellationToken);

    // The position of a message, or null when the conversation has no such message inside the window the caller may
    // read. A message of another conversation, one sent before the caller joined, or one sent after they left, is "no
    // such message", so a cursor reveals nothing about what the caller may not read.
    Task<MessageCursor?> FindCursorAsync(
        Guid conversationId, Guid messageId, MemberWindow window, CancellationToken cancellationToken);

    // The three reads below return at most "take" messages sent inside the window, oldest first. A deleted message is
    // returned with its content removed, so the timeline has no gaps.

    // The newest messages of the conversation.
    Task<List<MessageDto>> GetLatestAsync(
        Guid conversationId, MemberWindow window, int take, CancellationToken cancellationToken);

    // The messages just older than the cursor: the newest "take" of those positioned before it.
    Task<List<MessageDto>> GetBeforeAsync(
        Guid conversationId, MemberWindow window, MessageCursor cursor, int take, CancellationToken cancellationToken);

    // The messages just newer than the cursor: the oldest "take" of those positioned after it.
    Task<List<MessageDto>> GetAfterAsync(
        Guid conversationId, MemberWindow window, MessageCursor cursor, int take, CancellationToken cancellationToken);
}
