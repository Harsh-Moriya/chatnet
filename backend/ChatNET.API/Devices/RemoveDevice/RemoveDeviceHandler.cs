using ChatNET.API.Common.Exceptions;
using ChatNET.API.Devices.Persistence;
using MediatR;

namespace ChatNET.API.Devices.RemoveDevice;

public class RemoveDeviceHandler : IRequestHandler<RemoveDeviceCommand, Unit>
{
    private readonly IDeviceRepository _devices;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoveDeviceHandler> _logger;

    public RemoveDeviceHandler(IDeviceRepository devices, TimeProvider time, ILogger<RemoveDeviceHandler> logger)
    {
        _devices = devices;
        _time = time;
        _logger = logger;
    }

    // A device that is not the caller's, does not exist, or is already removed all answer the same 404, so ids reveal nothing.
    // Removing a device does not by itself change the keys: a device that kept the private keys still holds them, which is
    // why the person's other devices move to new keys when they remove one that was lost.
    public async Task<Unit> Handle(RemoveDeviceCommand request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        if (!await _devices.RemoveAsync(request.UserId, request.DeviceId, now, cancellationToken))
            throw new NotFoundException("Device", request.DeviceId);

        _logger.LogInformation("User {UserId} removed device {DeviceId}; the sessions of that device ended", request.UserId, request.DeviceId);
        return Unit.Value;
    }
}
