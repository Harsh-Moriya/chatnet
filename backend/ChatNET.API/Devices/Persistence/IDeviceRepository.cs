using ChatNET.API.Devices.Models;

namespace ChatNET.API.Devices.Persistence;

public interface IDeviceRepository
{
    // Stores a device unless the person already has "maxDevices" that are not removed. Counting and inserting are one
    // transaction behind a lock on the person, so devices registered together cannot all fit under the limit and then exceed it.
    // Returns false at the limit.
    Task<bool> TryAddAsync(Device device, int maxDevices, CancellationToken cancellationToken);

    // The devices of a person that are not removed, oldest first.
    Task<List<Device>> GetActiveAsync(Guid userId, CancellationToken cancellationToken);

    // Marks one of the person's own devices removed and ends every session that was signed in on it, together or not at all. Another
    // person's device, an unknown one and one already removed are all false. Returns true when the device was removed now.
    Task<bool> RemoveAsync(Guid userId, Guid deviceId, DateTime now, CancellationToken cancellationToken);

    // The id back if it names a device of the person that is not removed, otherwise null. A session is only ever tied to a device
    // this way, so a request cannot tie itself to someone else's.
    Task<Guid?> FindActiveIdAsync(Guid userId, Guid? deviceId, CancellationToken cancellationToken);

    Task TouchAsync(Guid deviceId, DateTime now, CancellationToken cancellationToken);
}
