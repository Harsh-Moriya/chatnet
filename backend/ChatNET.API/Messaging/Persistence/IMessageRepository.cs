using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;

namespace ChatNET.API.Messaging.Persistence;

public interface IMessageRepository
{
    // Saves the message, the notifications it causes and its conversation's UpdatedAt (moved to the message's
    // CreatedAt), as one atomic write: either all of it happens or none of it does. Returns false, saving nothing, when the message
    // carries a client id that this sender already used in this conversation: another request sent it
    // first.
    Task<bool> TryAddAsync(Message message, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken);

    // The message this sender already sent in this conversation with this client id, or null.
    Task<MessageDto?> FindByClientMessageIdAsync(
        Guid conversationId, Guid senderId, Guid clientMessageId, CancellationToken cancellationToken);

    // The position of a message, or null when the conversation has no such message sent at or after
    // "since". A message of another conversation, or one sent before the caller joined, is "no such
    // message", so a cursor reveals nothing about what the caller may not read.
    Task<MessageCursor?> FindCursorAsync(
        Guid conversationId, Guid messageId, DateTime since, CancellationToken cancellationToken);

    // The three reads below return at most "take" messages sent at or after "since", oldest first. A
    // deleted message is returned with its content removed, so the timeline has no gaps.

    // The newest messages of the conversation.
    Task<List<MessageDto>> GetLatestAsync(
        Guid conversationId, DateTime since, int take, CancellationToken cancellationToken);

    // The messages just older than the cursor: the newest "take" of those positioned before it.
    Task<List<MessageDto>> GetBeforeAsync(
        Guid conversationId, DateTime since, MessageCursor cursor, int take, CancellationToken cancellationToken);

    // The messages just newer than the cursor: the oldest "take" of those positioned after it.
    Task<List<MessageDto>> GetAfterAsync(
        Guid conversationId, DateTime since, MessageCursor cursor, int take, CancellationToken cancellationToken);
}
