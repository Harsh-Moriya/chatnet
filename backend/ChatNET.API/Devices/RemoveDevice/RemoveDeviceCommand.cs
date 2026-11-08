using MediatR;

namespace ChatNET.API.Devices.RemoveDevice;

// Declared as IRequest<Unit> so the validator runs (see MarkReadCommand).
public record RemoveDeviceCommand(Guid UserId, Guid DeviceId) : IRequest<Unit>;
