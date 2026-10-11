using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Conversations.Persistence;

public class ConversationRepository : IConversationRepository
{
    private const int PreviewLength = 100;

    private readonly AppDbContext _db;

    public ConversationRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<Conversation?> GetDirectByKeyAsync(string directKey, CancellationToken cancellationToken)
        => _db.Conversations.FirstOrDefaultAsync(c => c.DirectKey == directKey, cancellationToken);

    public async Task<bool> TryAddDirectAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        _db.Conversations.Add(conversation);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The failed insert stays in the change tracker; clear it so the caller can
            // safely run further queries on this context.
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task AddAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConversationDto?> GetForViewerAsync(
        Guid conversationId, Guid viewerId, CancellationToken cancellationToken)
    {
        var rows = await ProjectForViewer(
                _db.Conversations.Where(c => c.Id == conversationId), viewerId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).FirstOrDefault();
    }

    public async Task<List<ConversationDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await ProjectForViewer(
                _db.Conversations
                    .Where(c => c.Members.Any(m => m.UserId == userId && m.LeftAt == null))
                    .OrderByDescending(c => c.UpdatedAt),
                userId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public Task<bool> IsActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
        => _db.ConversationMembers.AnyAsync(
            m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null,
            cancellationToken);

    public Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

    public Task<List<Guid>> GetActiveConversationIdsForUserAsync(Guid userId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .Where(m => m.UserId == userId && m.LeftAt == null)
            .Select(m => m.ConversationId)
            .ToListAsync(cancellationToken);

    // One query per call regardless of how many conversations match: the other
    // participant's name and the latest message are subqueries inside the same SELECT,
    // so there is no N+1 round trip per sidebar row.
    private IQueryable<ConversationRow> ProjectForViewer(IQueryable<Conversation> source, Guid viewerId)
        => source.Select(c => new ConversationRow(
            c.Id,
            c.Type,
            c.Type == ConversationType.Direct
                ? c.Members
                    .Where(m => m.UserId != viewerId)
                    .Select(m => m.User.DisplayName)
                    .FirstOrDefault()
                : c.Name,
            c.AvatarUrl,
            c.UpdatedAt,
            _db.Messages
                .Where(m => m.ConversationId == c.Id)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new LastMessageRow(m.SenderId, m.IsDeleted ? null : m.Content, m.CreatedAt))
                .FirstOrDefault()));

    private static ConversationDto ToDto(ConversationRow row)
    {
        var last = row.LastMessage is null
            ? null
            : new LastMessageDto(
                row.LastMessage.SenderId,
                Truncate(row.LastMessage.Content),
                row.LastMessage.CreatedAt);

        return new ConversationDto(row.Id, row.Type, row.Name ?? "Unknown", row.AvatarUrl, row.UpdatedAt, last);
    }

    private static string? Truncate(string? content)
        => content is { Length: > PreviewLength } ? content[..PreviewLength] : content;

    private sealed record ConversationRow(
        Guid Id,
        ConversationType Type,
        string? Name,
        string? AvatarUrl,
        DateTime UpdatedAt,
        LastMessageRow? LastMessage);

    private sealed record LastMessageRow(Guid SenderId, string? Content, DateTime CreatedAt);
}
