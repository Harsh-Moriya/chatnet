using ChatNET.API.Common.Persistence;
using ChatNET.API.Messaging.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Messaging.Persistence;

public class AttachmentRepository : IAttachmentRepository
{
    private readonly AppDbContext _db;

    public AttachmentRepository(AppDbContext db)
    {
        _db = db;
    }

    // Uses the index on MessageId. The id is the second sort key only so that two files stamped alike always
    // come in the same order; the stamps themselves already follow the order the files were attached in.
    public Task<List<MessageAttachment>> GetForMessagesAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
        => _db.MessageAttachments
            .AsNoTracking()
            .Where(a => messageIds.Contains(a.MessageId))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

    public async Task<AttachmentAccess?> FindForReadAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        var found = await _db.MessageAttachments
            .AsNoTracking()
            .Where(a => a.Id == attachmentId)
            .Select(a => new
            {
                Attachment = a,
                a.Message.ConversationId,
                MessageCreatedAt = a.Message.CreatedAt,
                a.Message.IsDeleted,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return found is null
            ? null
            : new AttachmentAccess(found.Attachment, found.ConversationId, found.MessageCreatedAt, found.IsDeleted);
    }
}
