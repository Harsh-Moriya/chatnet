using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Messaging.Persistence;

// An attachment with what is needed to decide whether someone may read it.
public sealed record AttachmentAccess(
    MessageAttachment Attachment, Guid ConversationId, DateTime MessageCreatedAt, bool MessageDeleted);

public interface IAttachmentRepository
{
    // The files of all the given messages, in one query whatever the number of messages, each message's files
    // in the order they were attached.
    Task<List<MessageAttachment>> GetForMessagesAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);

    // Null when there is no such attachment.
    Task<AttachmentAccess?> FindForReadAsync(Guid attachmentId, CancellationToken cancellationToken);
}
