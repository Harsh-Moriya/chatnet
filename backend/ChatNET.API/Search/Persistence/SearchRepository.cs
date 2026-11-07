using System.Text;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Search.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Search.Persistence;

public class SearchRepository : ISearchRepository
{
    // The markers ts_headline puts around a match: two characters from the Unicode private use area, which text does not
    // contain in practice, and which are replaced by a space in the text before it is cut, so a message that does hold them
    // cannot fake a match.
    private const char MatchStart = '';
    private const char MatchEnd = '';

    private readonly AppDbContext _db;

    public SearchRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<MessageCursor?> FindCursorAsync(
        Guid userId, Guid? conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var found = await (
            from message in _db.Messages.AsNoTracking()
            join mine in _db.ConversationMembers.AsNoTracking() on message.ConversationId equals mine.ConversationId
            where message.Id == messageId
                  && mine.UserId == userId
                  && (conversationId == null || message.ConversationId == conversationId)
                  && message.CreatedAt >= mine.JoinedAt
                  && (mine.LeftAt == null || message.CreatedAt <= mine.LeftAt)
            select new { message.CreatedAt, message.Id })
            .FirstOrDefaultAsync(cancellationToken);

        return found is null ? null : new MessageCursor(found.CreatedAt, found.Id);
    }

    public async Task<List<SearchHitDto>> SearchAsync(
        Guid userId, Guid? conversationId, SearchQueryText text, MessageCursor? after, int take,
        CancellationToken cancellationToken)
    {
        var parameters = new List<NpgsqlParameter> { new("viewerId", userId), new("take", take) };

        if (text.HasHead) parameters.Add(new NpgsqlParameter("head", text.Head));
        if (text.PrefixQuery is { } prefix) parameters.Add(new NpgsqlParameter("prefix", prefix));
        if (conversationId is { } conversation) parameters.Add(new NpgsqlParameter("conversationId", conversation));
        if (after is { } cursor)
        {
            parameters.Add(new NpgsqlParameter("cursorAt", cursor.CreatedAt));
            parameters.Add(new NpgsqlParameter("cursorId", cursor.Id));
        }

        var rows = await _db.Database
            .SqlQueryRaw<SearchRow>(BuildSql(text, conversationId is not null, after is not null), parameters.ToArray())
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    // ##### The query #####
    // Written as SQL by hand, as the conversation list is, for the same reason: the obvious form reads far too much.
    // Each conversation the person is in is searched on its own, in a lateral subquery, over the window they may read, and
    // the matches of all of them are merged and cut to "take". Within a conversation there are two phases, because Postgres
    // cannot be trusted to choose between its two ways of searching (it guesses how many messages match, and guesses badly for
    // a word typed as a prefix):
    //
    //   recent   the newest RecentBudget messages of the window, read in order from the (ConversationId, CreatedAt) index. Their
    //            text, and the names of their files, are checked here. A common word has its "take" matches within a few
    //            rows, and a small conversation is covered entirely. At most RecentBudget rows are read per conversation, and
    //            the check of the text happens on those rows only.
    //   deep     the whole window, searched through the full text index on the conversation and the words
    //            together (the migration), so that the rows read are the matches and not the history. It runs only when the
    //            recent phase was full (there is an older part) and found fewer than "take" matches (it needs more). The few messages
    //            it finds that the recent phase already found are one row in the union.
    //
    // A message matches by its text or by the name of one of its files; the file names are checked in the recent phase only,
    // because a file has no conversation of its own to index by. The text of a file name is split at dots, dashes and
    // underscores first, so "report.pdf" is found by "pdf". The file branch leaves out messages whose text matched, so a
    // message is not listed twice, and names the first file that matched.
    //
    // Only constants are concatenated here; every value reaches the database as a parameter. The index in the migration
    // must match Words exactly, or Postgres cannot use it.
    private const int RecentBudget = 300;

    private static string Words(string alias) => $"""to_tsvector('simple', COALESCE({alias}."Content", ''))""";

    private const string FileWords = """to_tsvector('simple', regexp_replace(a."FileName", '[._-]+', ' ', 'g'))""";

    // "CreatedAt <= t" first, so the index walk starts at the cursor; the id decides among messages stamped exactly t.
    private const string AfterCursor = """
        AND m."CreatedAt" <= @cursorAt AND (m."CreatedAt" < @cursorAt OR m."Id" < @cursorId)
        """;

    private static string BuildSql(SearchQueryText text, bool inOneConversation, bool afterCursor)
    {
        var query = text switch
        {
            { HasHead: true, PrefixQuery: not null } => "(websearch_to_tsquery('simple', @head) && to_tsquery('simple', @prefix))",
            { PrefixQuery: not null } => "to_tsquery('simple', @prefix)",
            _ => "websearch_to_tsquery('simple', @head)",
        };

        var cursor = afterCursor ? AfterCursor : string.Empty;
        var onlyThisConversation = inOneConversation ? """AND mine."ConversationId" = @conversationId""" : string.Empty;

        return $$"""
            WITH page AS MATERIALIZED (
                SELECT hit."Id", hit."ConversationId", hit."SenderId", hit."CreatedAt", hit."Content", hit."FileName"
                FROM "ConversationMembers" mine
                CROSS JOIN LATERAL (
                    WITH recent AS MATERIALIZED (
                        SELECT m."Id", m."ConversationId", m."SenderId", m."CreatedAt", m."Content", m."IsDeleted"
                        FROM "Messages" m
                        WHERE m."ConversationId" = mine."ConversationId"
                          AND m."CreatedAt" >= mine."JoinedAt"
                          AND m."CreatedAt" <= COALESCE(mine."LeftAt", 'infinity'::timestamptz)
                          {{cursor}}
                        ORDER BY m."CreatedAt" DESC, m."Id" DESC
                        LIMIT {{RecentBudget}}),
                    shallow AS MATERIALIZED (
                        (SELECT r."Id", r."ConversationId", r."SenderId", r."CreatedAt", r."Content", NULL::text AS "FileName"
                         FROM recent r
                         WHERE NOT r."IsDeleted" AND {{Words("r")}} @@ {{query}})
                        UNION ALL
                        (SELECT r."Id", r."ConversationId", r."SenderId", r."CreatedAt", r."Content",
                                (SELECT a."FileName" FROM "MessageAttachments" a
                                 WHERE a."MessageId" = r."Id" AND {{FileWords}} @@ {{query}}
                                 ORDER BY a."CreatedAt", a."Id" LIMIT 1) AS "FileName"
                         FROM recent r
                         WHERE NOT r."IsDeleted"
                           AND NOT ({{Words("r")}} @@ {{query}})
                           AND EXISTS (SELECT 1 FROM "MessageAttachments" a
                                       WHERE a."MessageId" = r."Id" AND {{FileWords}} @@ {{query}}))
                        ORDER BY "CreatedAt" DESC, "Id" DESC
                        LIMIT @take)
                    SELECT * FROM shallow
                    UNION
                    (SELECT m."Id", m."ConversationId", m."SenderId", m."CreatedAt", m."Content", NULL::text AS "FileName"
                     FROM "Messages" m
                     WHERE m."ConversationId" = mine."ConversationId"
                       AND tstzrange(mine."JoinedAt", mine."LeftAt", '[]') @> m."CreatedAt"
                       AND NOT m."IsDeleted"
                       AND {{Words("m")}} @@ {{query}}
                       AND (SELECT count(*) FROM recent) = {{RecentBudget}}
                       AND (SELECT count(*) FROM shallow) < @take
                     ORDER BY m."CreatedAt" DESC, m."Id" DESC
                     LIMIT @take)
                ) hit
                WHERE mine."UserId" = @viewerId
                  {{onlyThisConversation}}
                ORDER BY hit."CreatedAt" DESC, hit."Id" DESC
                LIMIT @take
            )
            SELECT p."Id" AS "MessageId",
                   p."ConversationId" AS "ConversationId",
                   CASE WHEN c."Type" = 'Direct' THEN other."DisplayName" ELSE c."Name" END AS "ConversationName",
                   c."Type" AS "ConversationType",
                   p."SenderId" AS "SenderId",
                   u."DisplayName" AS "SenderDisplayName",
                   p."CreatedAt" AS "CreatedAt",
                   CASE WHEN p."FileName" IS NOT NULL OR p."Content" = '' THEN NULL
                        ELSE ts_headline('simple',
                                         replace(replace(p."Content", chr(57344), ' '), chr(57345), ' '),
                                         {{query}},
                                         format('StartSel=%s,StopSel=%s,MaxWords=20,MinWords=8', chr(57344), chr(57345)))
                   END AS "Snippet",
                   p."FileName" AS "FileName"
            FROM page p
            JOIN "Conversations" c ON c."Id" = p."ConversationId"
            JOIN "AspNetUsers" u ON u."Id" = p."SenderId"
            LEFT JOIN LATERAL (
                SELECT ou."DisplayName"
                FROM "ConversationMembers" cm
                JOIN "AspNetUsers" ou ON ou."Id" = cm."UserId"
                WHERE c."Type" = 'Direct' AND cm."ConversationId" = c."Id" AND cm."UserId" <> @viewerId
                LIMIT 1
            ) other ON TRUE
            ORDER BY p."CreatedAt" DESC, p."Id" DESC
            """;
    }

    private static SearchHitDto ToDto(SearchRow row)
        => new(
            row.MessageId, row.ConversationId, row.ConversationName ?? "Unknown", Enum.Parse<ConversationType>(row.ConversationType),
            row.SenderId, row.SenderDisplayName, row.CreatedAt, Parts(row.Snippet), row.FileName);

    // The headline cut into the pieces that matched and the text between them.
    public static List<SnippetPart> Parts(string? headline)
    {
        var parts = new List<SnippetPart>();
        if (string.IsNullOrEmpty(headline))
            return parts;

        var buffer = new StringBuilder();
        var matching = false;

        void Flush()
        {
            if (buffer.Length > 0) parts.Add(new SnippetPart(buffer.ToString(), matching));
            buffer.Clear();
        }

        foreach (var character in headline)
        {
            if (character == MatchStart)
            {
                Flush();
                matching = true;
            }
            else if (character == MatchEnd)
            {
                Flush();
                matching = false;
            }
            else
            {
                buffer.Append(character);
            }
        }

        Flush();
        return parts;
    }

    // One row of the query above. Property names match the column aliases, which is how EF maps a raw query to a type.
    private sealed class SearchRow
    {
        public Guid MessageId { get; set; }
        public Guid ConversationId { get; set; }
        public string? ConversationName { get; set; }
        public string ConversationType { get; set; } = string.Empty;
        public Guid SenderId { get; set; }
        public string SenderDisplayName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? Snippet { get; set; }
        public string? FileName { get; set; }
    }
}
