using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Conversations.Persistence;

public class ConversationRepository : IConversationRepository
{
    private const int PreviewLength = 100;

    // The unread count stops here, so the cost of a conversation does not grow with how far behind
    // the member is. Clients show it as "99+".
    public const int UnreadCap = 100;

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
        var rows = await _db.Database
            .SqlQueryRaw<ConversationRow>(
                ConversationSelect + ForOneConversation,
                new NpgsqlParameter("viewerId", viewerId),
                new NpgsqlParameter("conversationId", conversationId),
                new NpgsqlParameter("unreadCap", UnreadCap))
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).FirstOrDefault();
    }

    public async Task<List<ConversationDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await _db.Database
            .SqlQueryRaw<ConversationRow>(
                ConversationSelect + ForActiveMemberNewestFirst,
                new NpgsqlParameter("viewerId", userId),
                new NpgsqlParameter("unreadCap", UnreadCap))
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public Task<bool> IsActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
        => _db.ConversationMembers.AnyAsync(
            m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null,
            cancellationToken);

    public Task<DateTime?> GetActiveMemberJoinedAtAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null)
            .Select(m => (DateTime?)m.JoinedAt)
            .FirstOrDefaultAsync(cancellationToken);

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

    public Task<List<Guid>> GetContactIdsAsync(Guid userId, CancellationToken cancellationToken)
        => (from mine in _db.ConversationMembers
            join other in _db.ConversationMembers on mine.ConversationId equals other.ConversationId
            where mine.UserId == userId && mine.LeftAt == null
                  && other.UserId != userId && other.LeftAt == null
            select other.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

    // One UPDATE that carries its own "only if it moves forward" condition. Two requests racing for
    // the same member both reach the row, Postgres makes the second wait for the first and then
    // checks the condition against the value the first wrote, so the position can never go back
    // and no lock or transaction is needed in the application.
    public async Task<bool> AdvanceLastReadAsync(
        Guid conversationId, Guid userId, DateTime readAt, CancellationToken cancellationToken)
    {
        var updated = await _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId
                        && m.UserId == userId
                        && m.LeftAt == null
                        && (m.LastReadAt == null || m.LastReadAt < readAt))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadAt, readAt), cancellationToken);

        return updated > 0;
    }

    public Task<List<MemberDto>> GetActiveMembersAsync(Guid conversationId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId)
            .Select(m => new MemberDto(m.UserId, m.User.DisplayName, m.User.AvatarUrl, m.Role, m.LastReadAt))
            .ToListAsync(cancellationToken);

    // ##### Conversation list and detail queries #####
    // Written as SQL by hand because the obvious LINQ form is slow. EF Core translates "latest
    // message of each conversation" to ROW_NUMBER() over the messages of EVERY conversation and only
    // then joins to the caller's conversations, so each sidebar load read the whole Messages table
    // (measured: 600,000 rows, about 530 ms). A LEFT JOIN LATERAL runs a small lookup per conversation
    // instead: the index on (ConversationId, CreatedAt DESC) returns the newest message directly,
    // so the cost depends on the caller's conversations, not on the size of the system (about 1 ms).
    // EF cannot express a lateral join in LINQ. Only constants are concatenated below; every value
    // reaches the database as a parameter. A test checks how many Messages rows the query reads.
    // The reasoning and measurements are in note 08.
    //
    // Both the preview and the count only look at messages sent since the viewer joined, the same
    // window history uses (D-90), so nothing from before they joined shows up in the sidebar.
    //
    // The unread count is a correlated subquery over the same index: the messages of this conversation
    // since the viewer read it (or since they joined), from other people, counted by a query that
    // stops at @unreadCap rows. The viewer's own messages cannot pile up in front of the count, because
    // sending moves the sender's read position to the message (MessageRepository.AddAsync).
    private const string ConversationSelect = """
        SELECT c."Id"        AS "Id",
               c."Type"      AS "Type",
               CASE WHEN c."Type" = 'Direct' THEN other."DisplayName" ELSE c."Name" END AS "Name",
               c."AvatarUrl" AS "AvatarUrl",
               other."UserId" AS "OtherUserId",
               c."UpdatedAt" AS "UpdatedAt",
               lm."SenderId" AS "LastMessageSenderId",
               CASE WHEN lm."IsDeleted" THEN NULL ELSE lm."Content" END AS "LastMessageContent",
               lm."CreatedAt" AS "LastMessageCreatedAt",
               CASE WHEN lm."Id" IS NULL OR lm."IsDeleted" THEN 0
                    ELSE (SELECT COUNT(*)::int FROM "MessageAttachments" a WHERE a."MessageId" = lm."Id") END
                   AS "LastMessageAttachmentCount",
               (SELECT COUNT(*)::int
                FROM (SELECT 1
                      FROM "Messages" um
                      WHERE um."ConversationId" = c."Id"
                        AND um."CreatedAt" >= mine."JoinedAt"
                        AND um."CreatedAt" > COALESCE(mine."LastReadAt", '-infinity'::timestamptz)
                        AND um."SenderId" <> @viewerId
                      LIMIT @unreadCap) AS counted) AS "UnreadCount"
        FROM "Conversations" c
        JOIN "ConversationMembers" mine
          ON mine."ConversationId" = c."Id" AND mine."UserId" = @viewerId
        LEFT JOIN LATERAL (
            SELECT cm."UserId", u."DisplayName"
            FROM "ConversationMembers" cm
            JOIN "AspNetUsers" u ON u."Id" = cm."UserId"
            WHERE c."Type" = 'Direct'
              AND cm."ConversationId" = c."Id" AND cm."UserId" <> @viewerId
            LIMIT 1
        ) other ON TRUE
        LEFT JOIN LATERAL (
            SELECT m."Id", m."SenderId", m."Content", m."IsDeleted", m."CreatedAt"
            FROM "Messages" m
            WHERE m."ConversationId" = c."Id"
              AND m."CreatedAt" >= mine."JoinedAt"
            ORDER BY m."CreatedAt" DESC
            LIMIT 1
        ) lm ON TRUE
        """;

    private const string ForActiveMemberNewestFirst = """
         WHERE mine."LeftAt" IS NULL
         ORDER BY c."UpdatedAt" DESC
        """;

    private const string ForOneConversation = """
         WHERE c."Id" = @conversationId
        """;

    private static ConversationDto ToDto(ConversationRow row)
    {
        var last = row.LastMessageSenderId is { } senderId && row.LastMessageCreatedAt is { } createdAt
            ? new LastMessageDto(senderId, Truncate(row.LastMessageContent), createdAt, row.LastMessageAttachmentCount)
            : null;

        return new ConversationDto(
            row.Id, Enum.Parse<ConversationType>(row.Type), row.Name ?? "Unknown", row.AvatarUrl, row.UpdatedAt, last,
            row.UnreadCount, row.OtherUserId);
    }

    private static string? Truncate(string? content)
        => content is { Length: > PreviewLength } ? content[..PreviewLength] : content;

    // One row of the queries above. Property names match the column aliases in the SQL, which is how
    // EF maps a raw query to a type.
    private sealed class ConversationRow
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? AvatarUrl { get; set; }
        public DateTime UpdatedAt { get; set; }
        public Guid? LastMessageSenderId { get; set; }
        public string? LastMessageContent { get; set; }
        public DateTime? LastMessageCreatedAt { get; set; }
        public int LastMessageAttachmentCount { get; set; }
        public int UnreadCount { get; set; }
        public Guid? OtherUserId { get; set; }
    }
}
