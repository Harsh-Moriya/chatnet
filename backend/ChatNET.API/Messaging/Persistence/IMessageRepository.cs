using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Messaging.Persistence;

// How storing a message went. Nothing is saved unless it is Added.
public enum AddOutcome
{
    Added,

    // This sender already used the client id in this conversation: another request sent it first.
    Repeat,

    // The conversation is not in the epoch the message was sealed in (another was stored, or it has none).
    EpochStale,

    // The conversation is in that epoch, and a removal or a leave marked it for replacement.
    EpochDue,
}

// How an edit went. Nothing is saved unless it is Edited.
public enum EditOutcome
{
    Edited,
    EpochStale,
    EpochDue,
    Deleted,
    TooLate,

    // The message was edited somewhere else first, so the edit count the new text was signed for is not the next one.
    EditCountStale,
}

public interface IMessageRepository
{
    // Saves the message, the notifications it causes, the outbox rows for their pushes and its conversation's
    // UpdatedAt (moved to the message's CreatedAt), as one atomic write: either all of it happens or none of it
    // does. The conversation must be in the epoch the message was sealed in and not marked for replacement, which one
    // conditional UPDATE decides together with whoever changes the conversation's keys. The same statement gives the message
    // the next change number of the conversation (set on the message that was passed in).
    Task<AddOutcome> TryAddAsync(
        Message message, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken);

    // The message as it is stored (a deleted one without its content, and without its files), or null. Files are
    // not included: AttachmentLoader adds them.
    Task<MessageDto?> FindByIdAsync(Guid messageId, CancellationToken cancellationToken);

    // The message with what editing or deleting it depends on, or null when the conversation has no such message sent at
    // or after "since". A message from before the caller joined is "no such message", as in history.
    Task<MessageForChange?> FindForChangeAsync(
        Guid conversationId, Guid messageId, DateTime since, CancellationToken cancellationToken);

    // Replaces the text of a message that is not deleted and was sent after notBefore, which was last edited editCount - 1 times,
    // counts the edit, gives the message the next change number of the conversation, and saves the notifications and outbox rows
    // that come with it, in one transaction. The conversation must be in the epoch the new text was sealed in and not marked for
    // replacement. One conditional UPDATE decides, so an edit that loses to a delete or to another edit (or arrives after the
    // window closed) changes nothing and says why, and takes no number.
    Task<EditOutcome> TryEditAsync(
        Guid conversationId, Guid messageId, byte[] ciphertext, int keyEpoch, int editCount, DateTime editedAt, DateTime notBefore,
        IReadOnlyList<Guid> mentionedUserIds, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken);

    // Marks a message that is not deleted as deleted, removes its text (the key period it was sealed in is kept), gives it the next
    // change number of its conversation, and saves the outbox rows, in one transaction.
    // False, with nothing saved and no number taken, when it already was deleted.
    Task<bool> TryDeleteAsync(
        Guid conversationId, Guid messageId, Guid deletedById, DateTime deletedAt, IReadOnlyList<OutboxMessage> outbox,
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

    // The latest change number of the conversation, or null when there is no such conversation.
    Task<long?> GetChangeSeqAsync(Guid conversationId, CancellationToken cancellationToken);

    // The messages sent inside the window whose latest change is numbered above "since" and no higher than "upTo", in the order of
    // that number, at most "take". A deleted message is returned with its content removed. "upTo" is what the caller read as the
    // conversation's counter before this read, and it is what makes a cursor taken from the answer safe: every number up to a
    // counter that has been read is committed.
    Task<List<MessageDto>> GetChangesAsync(
        Guid conversationId, MemberWindow window, long since, long upTo, int take, CancellationToken cancellationToken);
}
