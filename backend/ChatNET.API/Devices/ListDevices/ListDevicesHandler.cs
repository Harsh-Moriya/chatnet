using ChatNET.API.Common.Time;
using ChatNET.API.Devices.Models;
using ChatNET.API.Devices.Persistence;
using MediatR;

namespace ChatNET.API.Devices.ListDevices;

public class ListDevicesHandler : IRequestHandler<ListDevicesQuery, List<DeviceDto>>
{
    private readonly IDeviceRepository _devices;

    public ListDevicesHandler(IDeviceRepository devices)
    {
        _devices = devices;
    }

    public async Task<List<DeviceDto>> Handle(ListDevicesQuery request, CancellationToken cancellationToken)
    {
        var devices = await _devices.GetActiveAsync(request.UserId, cancellationToken);

        return devices
            .Select(d => new DeviceDto(d.Id, d.Kind, d.Name, d.CreatedAt.TruncateToMicroseconds(), d.LastActiveAt.TruncateToMicroseconds()))
            .ToList();
    }
}
