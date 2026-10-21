using System.Linq.Expressions;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        m.ClientMessageId);

    private readonly AppDbContext _db;

    public MessageRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryAddAsync(Message message, CancellationToken cancellationToken)
    {
        _db.Messages.Add(message);

        // The conversation row is never loaded. A stub carrying only its key and the new UpdatedAt is
        // attached and that one property is marked modified, so EF sends a single-column UPDATE next
        // to the INSERT. SaveChanges runs its statements in one transaction, which keeps the message
        // and the conversation's place in the sidebar from ever disagreeing.
        var conversation = new Conversation { Id = message.ConversationId, UpdatedAt = message.CreatedAt };
        _db.Conversations.Attach(conversation);
        _db.Entry(conversation).Property(c => c.UpdatedAt).IsModified = true;

        // Sending counts as reading: whoever sends has seen the conversation up to their message. The
        // same stub trick moves the sender's read position, in the same transaction. Besides being
        // what people expect, it keeps the unread count cheap, because the sender's own messages
        // never accumulate after their read position for the count to step over.
        var sender = new ConversationMember
        {
            ConversationId = message.ConversationId,
            UserId = message.SenderId,
            LastReadAt = message.CreatedAt,
        };
        _db.ConversationMembers.Attach(sender);
        _db.Entry(sender).Property(m => m.LastReadAt).IsModified = true;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsClientMessageClash(ex))
        {
            // The failed insert (and the two stubs next to it) stay in the change tracker; clear them so the
            // caller can run further queries on this context.
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    // Only a clash on the client id index means "already sent". Any other unique violation is a different
    // problem and must not be mistaken for a retry.
    public static bool IsClientMessageClash(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: MessageConfiguration.ClientMessageIndexName,
        };

    public Task<MessageDto?> FindByClientMessageIdAsync(
        Guid conversationId, Guid senderId, Guid clientMessageId, CancellationToken cancellationToken)
        => _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.SenderId == senderId && m.ClientMessageId == clientMessageId)
            .Select(ToDto)
            .FirstOrDefaultAsync(cancellationToken);

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
