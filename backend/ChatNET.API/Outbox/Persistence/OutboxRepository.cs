using ChatNET.API.Common.Persistence;
using ChatNET.API.Outbox.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Outbox.Persistence;

public class OutboxRepository : IOutboxRepository
{
    // The delay before a row is tried again doubles with each attempt, but stops growing at this many times the base.
    private const int MaxDelayFactor = 64;

    // The longest an error is kept; the column is that wide.
    private const int MaxErrorLength = 500;

    // One statement does the choosing and the holding. FOR UPDATE SKIP LOCKED in the inner query makes a relay that
    // runs at the same moment pass over the rows this one is taking instead of waiting for them or taking them too,
    // and the UPDATE moves their NextAttemptAt out, which is what keeps them from every later pass until the retry
    // is due. Nothing stays locked while the pushes are made: the row is held by that time, not by a transaction.
    // In the UPDATE a column on the right still has its old value, so the first attempt waits retryDelay, the second
    // twice that, and so on. Raw SQL because EF cannot express UPDATE ... RETURNING (D-76).
    private const string ClaimSql = """
        UPDATE "OutboxMessages" AS o
        SET "Attempts" = o."Attempts" + 1,
            "NextAttemptAt" = @now + (@retrySeconds * LEAST(POWER(2, o."Attempts"), @maxFactor)) * INTERVAL '1 second'
        WHERE o."Id" IN (
            SELECT "Id" FROM "OutboxMessages"
            WHERE "DispatchedAt" IS NULL AND "Attempts" < @maxAttempts AND "NextAttemptAt" <= @now
            ORDER BY "CreatedAt", "Id"
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED)
        RETURNING o."Id", o."Kind", o."Payload", o."CreatedAt", o."NextAttemptAt", o."DispatchedAt", o."Attempts", o."LastError"
        """;

    private readonly AppDbContext _db;

    public OutboxRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<OutboxMessage>> ClaimDueAsync(
        DateTime now, int batchSize, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        var claimed = await _db.OutboxMessages
            .FromSqlRaw(
                ClaimSql,
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("retrySeconds", retryDelay.TotalSeconds),
                new NpgsqlParameter("maxFactor", (double)MaxDelayFactor),
                new NpgsqlParameter("maxAttempts", maxAttempts),
                new NpgsqlParameter("batchSize", batchSize))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // UPDATE ... RETURNING gives the rows in no particular order.
        return claimed.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToList();
    }

    public async Task MarkDispatchedAsync(
        IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;

        await _db.OutboxMessages
            .Where(o => ids.Contains(o.Id) && o.DispatchedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.DispatchedAt, now), cancellationToken);
    }

    public async Task RecordFailureAsync(Guid id, string error, CancellationToken cancellationToken)
    {
        var kept = error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];

        await _db.OutboxMessages
            .Where(o => o.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LastError, kept), cancellationToken);
    }

    public Task<int> DeleteFinishedAsync(DateTime before, int maxAttempts, CancellationToken cancellationToken)
        => _db.OutboxMessages
            .Where(o => (o.DispatchedAt != null && o.DispatchedAt < before)
                        || (o.DispatchedAt == null && o.Attempts >= maxAttempts && o.CreatedAt < before))
            .ExecuteDeleteAsync(cancellationToken);
}
