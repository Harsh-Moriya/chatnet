using System.Linq.Expressions;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Messaging.Persistence;

public class MessageRepository : IMessageRepository
{
    // Only the page's senders are looked up, through the join, in the same query.
    private static readonly Expression<Func<Message, MessageDto>> ToDto = m => new MessageDto(
        m.Id,
        m.ConversationId,
        m.SenderId,
        m.Sender.DisplayName,
        m.IsDeleted ? null : m.Content,
        m.CreatedAt,
        null);

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

    public async Task<MessageCursor?> FindCursorAsync(
        Guid conversationId, Guid messageId, DateTime since, CancellationToken cancellationToken)
    {
        var found = await _db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId && m.ConversationId == conversationId && m.CreatedAt >= since)
            .Select(m => new { m.CreatedAt, m.Id })
            .FirstOrDefaultAsync(cancellationToken);

        return found is null ? null : new MessageCursor(found.CreatedAt, found.Id);
    }

    public Task<List<MessageDto>> GetLatestAsync(
        Guid conversationId, DateTime since, int take, CancellationToken cancellationToken)
        => ReadNewestFirstAsync(InConversation(conversationId, since), take, cancellationToken);

    // Positions are compared as (CreatedAt, Id). "CreatedAt <= t" comes first and is what the index
    // on (ConversationId, CreatedAt DESC) uses to start its walk; the id comparison only decides
    // among messages stamped exactly t, so the rows it filters out are a handful at most.
    public Task<List<MessageDto>> GetBeforeAsync(
        Guid conversationId, DateTime since, MessageCursor cursor, int take, CancellationToken cancellationToken)
        => ReadNewestFirstAsync(
            InConversation(conversationId, since).Where(m =>
                m.CreatedAt <= cursor.CreatedAt && (m.CreatedAt < cursor.CreatedAt || m.Id.CompareTo(cursor.Id) < 0)),
            take,
            cancellationToken);

    public async Task<List<MessageDto>> GetAfterAsync(
        Guid conversationId, DateTime since, MessageCursor cursor, int take, CancellationToken cancellationToken)
    {
        // Reading forwards already yields reading order, so the page is not turned around.
        return await InConversation(conversationId, since)
            .Where(m => m.CreatedAt >= cursor.CreatedAt && (m.CreatedAt > cursor.CreatedAt || m.Id.CompareTo(cursor.Id) > 0))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(take)
            .Select(ToDto)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Message> InConversation(Guid conversationId, DateTime since)
        => _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.CreatedAt >= since);

    // Sorting newest first lets Postgres walk the (ConversationId, CreatedAt DESC) index from the
    // newest message and stop after "take" rows, whatever the size of the conversation. The id is the
    // second sort key so that messages with one timestamp always come in the same order, which is
    // the order the cursor comparison assumes. The page is turned around afterwards so the client
    // receives it in reading order.
    private static async Task<List<MessageDto>> ReadNewestFirstAsync(
        IQueryable<Message> messages, int take, CancellationToken cancellationToken)
    {
        var newestFirst = await messages
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(take)
            .Select(ToDto)
            .ToListAsync(cancellationToken);

        newestFirst.Reverse();
        return newestFirst;
    }
}
