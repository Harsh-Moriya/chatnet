using ChatNET.API.Common.Persistence;
using ChatNET.API.Devices.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Devices.Persistence;

public class DeviceRepository : IDeviceRepository
{
    private readonly AppDbContext _db;

    public DeviceRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryAddAsync(Device device, int maxDevices, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Held until the transaction ends. Without it two requests could both count nine devices and both insert the tenth.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"devices:" + device.UserId}, 0))", cancellationToken);

        var active = await _db.Devices.CountAsync(d => d.UserId == device.UserId && d.RemovedAt == null, cancellationToken);
        if (active >= maxDevices)
            return false;

        _db.Devices.Add(device);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<List<Device>> GetActiveAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Devices
            .AsNoTracking()
            .Where(d => d.UserId == userId && d.RemovedAt == null)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> RemoveAsync(Guid userId, Guid deviceId, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        if (!await DeviceRemoval.TryRemoveAsync(_db, userId, deviceId, now, cancellationToken))
            return false;

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<Guid?> FindActiveIdAsync(Guid userId, Guid? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is null)
            return null;

        var found = await _db.Devices
            .AsNoTracking()
            .AnyAsync(d => d.Id == deviceId && d.UserId == userId && d.RemovedAt == null, cancellationToken);

        return found ? deviceId : null;
    }

    public Task TouchAsync(Guid deviceId, DateTime now, CancellationToken cancellationToken) =>
        _db.Devices
            .Where(d => d.Id == deviceId && d.RemovedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastActiveAt, now), cancellationToken);
}
