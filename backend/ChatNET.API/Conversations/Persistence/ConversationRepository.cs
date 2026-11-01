using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Media;
using ChatNET.API.Messaging;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Conversations.Persistence;

public class ConversationRepository : IConversationRepository
{
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

    public async Task<bool> TryAddDirectAsync(
        Conversation conversation, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken)
    {
        _db.Conversations.Add(conversation);
        _db.OutboxMessages.AddRange(outbox);

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

    public async Task AddAsync(
        Conversation conversation, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken)
    {
        _db.Conversations.Add(conversation);
        _db.Notifications.AddRange(notifications);
        _db.OutboxMessages.AddRange(outbox);
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
                ConversationSelect + ForMemberNewestFirst,
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

    public Task<MemberWindow?> GetMemberWindowAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.UserId == userId)
            .Select(m => new MemberWindow(m.JoinedAt, m.LeftAt))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

    public Task<MemberRole?> GetActiveMemberRoleAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);

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
    // and no lock is needed in the application.
    //
    // The outbox row is saved in a transaction with the UPDATE, and only when the UPDATE moved something: a request
    // that moved nothing announces nothing. The transaction also holds the member's row until the row is saved,
    // which a second request for the same member waits for, as it already waited for the UPDATE.
    public async Task<bool> AdvanceLastReadAsync(
        Guid conversationId, Guid userId, DateTime readAt, OutboxMessage outbox, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var updated = await _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId
                        && m.UserId == userId
                        && m.LeftAt == null
                        && (m.LastReadAt == null || m.LastReadAt < readAt))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadAt, readAt), cancellationToken);

        if (updated == 0)
            return false;

        _db.OutboxMessages.Add(outbox);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    public Task<List<MemberDto>> GetActiveMembersAsync(Guid conversationId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId)
            .Select(m => new MemberDto(
                m.UserId, m.User.UserName!, m.User.DisplayName, AvatarKeys.PublicUrl(m.User.AvatarKey), m.Role, m.LastReadAt))
            .ToListAsync(cancellationToken);

    // A single UPDATE that is also the membership check: it touches the row of an active member and nothing else,
    // so a stranger or someone who left changes nothing and gets false. Setting the value a row already has still
    // matches it, which makes repeating a request harmless.
    public async Task<bool> SetMutedAsync(
        Guid conversationId, Guid userId, bool isMuted, CancellationToken cancellationToken)
        => await _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsMuted, isMuted), cancellationToken) > 0;

    public Task<DateTime?> GetLeftAtAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
        => _db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == userId)
            .Select(m => m.LeftAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ConversationUpdateDto?> GetUpdateAsync(Guid conversationId, CancellationToken cancellationToken)
        => _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId && c.Type == ConversationType.Group)
            .Select(c => new ConversationUpdateDto(c.Id, c.Name!, AvatarKeys.PublicUrl(c.AvatarKey)))
            .FirstOrDefaultAsync(cancellationToken);

    // A transaction-scoped advisory lock on the conversation, as registration takes one on an email: changes to the
    // same group go one at a time, each reading the rows after the one before it committed (a statement reads what was
    // committed when it starts), and changes to different groups never wait for each other. It is released by the
    // commit or the rollback, so a request that fails cannot leave it held.
    public async Task<IMembershipChange?> BeginMembershipChangeAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({conversationId.ToString()}, 0))", cancellationToken);

            var conversation = await _db.Conversations
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

            if (conversation is null)
            {
                await transaction.DisposeAsync();
                return null;
            }

            return new MembershipChange(_db, transaction, conversation);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

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
    // Both the preview and the count only look at the window the viewer may read, the same one history uses (D-90):
    // nothing from before they joined shows up in the sidebar, and for a group they left, nothing from after. Such a
    // conversation stays in the list, read-only: nothing in it is unread, its preview is the last message from before
    // they left, and it is ordered by when they left (or by its own last activity, if that was earlier). The upper
    // bound is written as a range on the same column the index is ordered by, so a group that went on for a long time
    // after the viewer left costs no more to show than one that did not.
    //
    // The unread count is a correlated subquery over the same index: the messages of this conversation
    // since the viewer read it (or since they joined), from other people, that were not deleted since, counted by a
    // query that stops at @unreadCap rows. The viewer's own messages cannot pile up in front of the count, because
    // sending moves the sender's read position to the message (MessageRepository.AddAsync).
    private const string ConversationSelect = """
        SELECT c."Id"        AS "Id",
               c."Type"      AS "Type",
               CASE WHEN c."Type" = 'Direct' THEN other."DisplayName" ELSE c."Name" END AS "Name",
               CASE WHEN c."Type" = 'Direct' THEN other."AvatarKey" ELSE c."AvatarKey" END AS "AvatarKey",
               other."UserId" AS "OtherUserId",
               LEAST(c."UpdatedAt", COALESCE(mine."LeftAt", 'infinity'::timestamptz)) AS "UpdatedAt",
               lm."SenderId" AS "LastMessageSenderId",
               CASE WHEN lm."IsDeleted" THEN NULL ELSE lm."Content" END AS "LastMessageContent",
               lm."CreatedAt" AS "LastMessageCreatedAt",
               CASE WHEN lm."Id" IS NULL OR lm."IsDeleted" THEN 0
                    ELSE (SELECT COUNT(*)::int FROM "MessageAttachments" a WHERE a."MessageId" = lm."Id") END
                   AS "LastMessageAttachmentCount",
               CASE WHEN mine."LeftAt" IS NOT NULL THEN 0
                    ELSE (SELECT COUNT(*)::int
                          FROM (SELECT 1
                                FROM "Messages" um
                                WHERE um."ConversationId" = c."Id"
                                  AND um."CreatedAt" >= mine."JoinedAt"
                                  AND um."CreatedAt" > COALESCE(mine."LastReadAt", '-infinity'::timestamptz)
                                  AND um."SenderId" <> @viewerId
                                  AND NOT um."IsDeleted"
                                LIMIT @unreadCap) AS counted) END AS "UnreadCount",
               mine."IsMuted" AS "IsMuted",
               mine."LeftAt" AS "LeftAt"
        FROM "Conversations" c
        JOIN "ConversationMembers" mine
          ON mine."ConversationId" = c."Id" AND mine."UserId" = @viewerId
        LEFT JOIN LATERAL (
            SELECT cm."UserId", u."DisplayName", u."AvatarKey"
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
              AND m."CreatedAt" <= COALESCE(mine."LeftAt", 'infinity'::timestamptz)
            ORDER BY m."CreatedAt" DESC
            LIMIT 1
        ) lm ON TRUE
        """;

    // Every conversation the user is in or was in, the most recently active first. The name in ORDER BY is the output column
    // above, which is c."UpdatedAt" for a conversation the user is in and the earlier of that and when they left for
    // one they left.
    private const string ForMemberNewestFirst = """
         ORDER BY "UpdatedAt" DESC
        """;

    private const string ForOneConversation = """
         WHERE c."Id" = @conversationId
        """;

    private static ConversationDto ToDto(ConversationRow row)
    {
        var last = row.LastMessageSenderId is { } senderId && row.LastMessageCreatedAt is { } createdAt
            ? new LastMessageDto(senderId, MessagePreview.Of(row.LastMessageContent), createdAt, row.LastMessageAttachmentCount)
            : null;

        return new ConversationDto(
            row.Id, Enum.Parse<ConversationType>(row.Type), row.Name ?? "Unknown", AvatarKeys.PublicUrl(row.AvatarKey), row.UpdatedAt, last,
            row.UnreadCount, row.OtherUserId, row.IsMuted, row.LeftAt);
    }

    // One row of the queries above. Property names match the column aliases in the SQL, which is how
    // EF maps a raw query to a type.
    private sealed class ConversationRow
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? AvatarKey { get; set; }
        public DateTime UpdatedAt { get; set; }
        public Guid? LastMessageSenderId { get; set; }
        public string? LastMessageContent { get; set; }
        public DateTime? LastMessageCreatedAt { get; set; }
        public int LastMessageAttachmentCount { get; set; }
        public int UnreadCount { get; set; }
        public Guid? OtherUserId { get; set; }
        public bool IsMuted { get; set; }
        public DateTime? LeftAt { get; set; }
    }
}
