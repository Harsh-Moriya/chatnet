using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Devices.Persistence;

// The two statements that remove a device, for whoever has a transaction open to put them in. They are separate from the
// repository's own transaction so that a change which removes a device as part of something larger (a new generation of the keys
// because the device was lost) commits or rolls back with it.
public static class DeviceRemoval
{
    // True when the device was removed now. Conditional on the owner and on not being removed already: of two requests removing the
    // same device exactly one does.
    public static async Task<bool> TryRemoveAsync(
        AppDbContext db, Guid userId, Guid deviceId, DateTime now, CancellationToken cancellationToken)
    {
        var removed = await db.Devices
            .Where(d => d.Id == deviceId && d.UserId == userId && d.RemovedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RemovedAt, now), cancellationToken);

        if (removed == 0)
            return false;

        // Every token of a session that is tied to the device carries it, and a renewal retires the token it replaces, so the one token
        // of each session that still works is found by the device. A renewal that is under way loses its conditional update against
        // the revoked token and the session does not come back.
        await db.RefreshTokens
            .Where(t => t.DeviceId == deviceId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

        return true;
    }

    // Every device of the person that is not removed, with the sessions signed in on each. How many were removed.
    public static async Task<int> RemoveAllAsync(AppDbContext db, Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var deviceIds = await db.Devices
            .Where(d => d.UserId == userId && d.RemovedAt == null)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        foreach (var deviceId in deviceIds)
            await TryRemoveAsync(db, userId, deviceId, now, cancellationToken);

        return deviceIds.Count;
    }
}
