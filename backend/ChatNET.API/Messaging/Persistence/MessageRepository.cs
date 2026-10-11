using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Messaging.Persistence;

public class MessageRepository : IMessageRepository
{
    private readonly AppDbContext _db;

    public MessageRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Message message, CancellationToken cancellationToken)
    {
        _db.Messages.Add(message);

        // The conversation row is never loaded. A stub carrying only its key and the new UpdatedAt is
        // attached and that one property is marked modified, so EF sends a single-column UPDATE next
        // to the INSERT. SaveChanges runs its statements in one transaction, which keeps the message
        // and the conversation's place in the sidebar from ever disagreeing.
        var conversation = new Conversation { Id = message.ConversationId, UpdatedAt = message.CreatedAt };
        _db.Conversations.Attach(conversation);
        _db.Entry(conversation).Property(c => c.UpdatedAt).IsModified = true;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<MessageDto>> GetLatestAsync(
        Guid conversationId, DateTime since, int take, CancellationToken cancellationToken)
    {
        // Sorting newest first lets Postgres walk the (ConversationId, CreatedAt DESC) index from the
        // newest message and stop after "take" rows, whatever the size of the conversation. The page
        // is turned around afterwards so the client receives it in reading order. Only the page's
        // senders are looked up, through the join, in the same query.
        var newestFirst = await _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.CreatedAt >= since)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new MessageDto(
                m.Id,
                m.ConversationId,
                m.SenderId,
                m.Sender.DisplayName,
                m.IsDeleted ? null : m.Content,
                m.CreatedAt,
                null))
            .ToListAsync(cancellationToken);

        newestFirst.Reverse();
        return newestFirst;
    }
}
