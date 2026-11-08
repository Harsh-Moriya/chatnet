using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Devices.Models;
using ChatNET.API.Devices.Persistence;
using ChatNET.API.Keys;
using ChatNET.API.Keys.Persistence;
using MediatR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Devices.RegisterDevice;

public class RegisterDeviceHandler : IRequestHandler<RegisterDeviceCommand, DeviceDto>
{
    private readonly IDeviceRepository _devices;
    private readonly IKeyRepository _keys;
    private readonly EncryptionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RegisterDeviceHandler> _logger;

    public RegisterDeviceHandler(
        IDeviceRepository devices, IKeyRepository keys, IOptions<EncryptionOptions> options, TimeProvider time,
        ILogger<RegisterDeviceHandler> logger)
    {
        _devices = devices;
        _keys = keys;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<DeviceDto> Handle(RegisterDeviceCommand request, CancellationToken cancellationToken)
    {
        // A device is a place the keys are held; with no keys set up there is nothing for it to hold.
        if (!await _keys.HasKeysAsync(request.UserId, cancellationToken))
            throw new ConflictException("Set up encryption before adding a device.");

        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();
        var device = new Device
        {
            Id = Guid.CreateVersion7(),
            UserId = request.UserId,
            Kind = DeviceRules.ParseKind(request.Kind!),
            Name = DeviceRules.CleanName(request.Name!),
            CreatedAt = now,
            LastActiveAt = now,
        };

        if (!await _devices.TryAddAsync(device, _options.MaxDevicesPerUser, cancellationToken))
            throw new ConflictException("This account already has the most devices it can have. Remove one first.");

        // Never the name, which the client chose.
        _logger.LogInformation("User {UserId} added device {DeviceId} of kind {Kind}", request.UserId, device.Id, device.Kind);

        return new DeviceDto(device.Id, device.Kind, device.Name, device.CreatedAt, device.LastActiveAt);
    }
}
