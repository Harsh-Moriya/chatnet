using ChatNET.API.Common.Persistence;
using ChatNET.API.Devices.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Devices.Persistence;

public class LinkRequestRepository : ILinkRequestRepository
{
    // Requests that expired more than this long ago are removed when their person opens a new one.
    private static readonly TimeSpan KeepExpired = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;

    public LinkRequestRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryAddAsync(DeviceLinkRequest request, int maxPending, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"link-requests:" + request.UserId}, 0))", cancellationToken);

        var staleBefore = now - KeepExpired;
        await _db.DeviceLinkRequests
            .Where(r => r.UserId == request.UserId && r.ExpiresAt < staleBefore)
            .ExecuteDeleteAsync(cancellationToken);

        var pending = await _db.DeviceLinkRequests.CountAsync(
            r => r.UserId == request.UserId && r.ApprovedAt == null && r.ExpiresAt > now, cancellationToken);
        if (pending >= maxPending)
            return false;

        // The unique index on the person and the code decides a clash; the number of rows says so without an exception.
        var inserted = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DeviceLinkRequests" ("Id", "UserId", "Code", "TemporaryPublicKey", "Kind", "Name", "CreatedAt", "ExpiresAt")
            VALUES ({request.Id}, {request.UserId}, {request.Code}, {request.TemporaryPublicKey}, {request.Kind.ToString()},
                    {request.Name}, {request.CreatedAt}, {request.ExpiresAt})
            ON CONFLICT ("UserId", "Code") DO NOTHING
            """, cancellationToken);

        if (inserted == 0)
            return false;

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<DeviceLinkRequest?> FindOpenByCodeAsync(Guid userId, string code, DateTime now, CancellationToken cancellationToken) =>
        _db.DeviceLinkRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.UserId == userId && r.Code == code && r.ApprovedAt == null && r.ExpiresAt > now, cancellationToken);

    public Task<DeviceLinkRequest?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        _db.DeviceLinkRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);

    public async Task<bool> TryApproveAsync(Guid userId, Guid id, byte[] payload, DateTime now, CancellationToken cancellationToken) =>
        await _db.DeviceLinkRequests
            .Where(r => r.Id == id && r.UserId == userId && r.ApprovedAt == null && r.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Payload, payload)
                .SetProperty(r => r.ApprovedAt, now), cancellationToken) == 1;

    // Raw SQL because EF cannot express UPDATE ... RETURNING. The payload is read and cleared by the same statement.
    public async Task<byte[]?> TryCollectAsync(Guid userId, Guid id, DateTime now, CancellationToken cancellationToken)
    {
        var payloads = await _db.Database.SqlQuery<byte[]>($"""
            UPDATE "DeviceLinkRequests" AS r
            SET "CollectedAt" = {now}, "Payload" = NULL
            FROM (SELECT "Id", "Payload" FROM "DeviceLinkRequests" WHERE "Id" = {id} AND "UserId" = {userId} FOR UPDATE) AS old
            WHERE r."Id" = old."Id" AND r."ApprovedAt" IS NOT NULL AND r."CollectedAt" IS NULL AND r."ExpiresAt" > {now}
            RETURNING old."Payload" AS "Value"
            """).ToListAsync(cancellationToken);

        return payloads.Count == 0 ? null : payloads[0];
    }
}
