using ChatNET.API.Devices.Models;
using MediatR;

namespace ChatNET.API.Devices.ListDevices;

public record ListDevicesQuery(Guid UserId) : IRequest<List<DeviceDto>>;
