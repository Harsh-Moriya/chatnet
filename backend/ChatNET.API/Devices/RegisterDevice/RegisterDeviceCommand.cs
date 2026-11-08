using ChatNET.API.Devices.Models;
using MediatR;

namespace ChatNET.API.Devices.RegisterDevice;

// A device that holds the keys of the person says what it is. Kind is Web, Ios or Android.
public record RegisterDeviceCommand(Guid UserId, string? Kind, string? Name) : IRequest<DeviceDto>;
