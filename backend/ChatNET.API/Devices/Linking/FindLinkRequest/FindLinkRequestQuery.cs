using ChatNET.API.Devices.Models;
using MediatR;

namespace ChatNET.API.Devices.Linking.FindLinkRequest;

// The open request of the person that has this code.
public record FindLinkRequestQuery(Guid UserId, string? Code) : IRequest<LinkRequestDto>;
