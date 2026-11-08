using ChatNET.API.Devices.Models;
using MediatR;

namespace ChatNET.API.Devices.Linking.GetLinkRequest;

// What the new device asks while it waits. Asking after approval hands the sealed secrets over, once.
public record GetLinkRequestQuery(Guid UserId, Guid RequestId) : IRequest<LinkRequestStatusDto>;
