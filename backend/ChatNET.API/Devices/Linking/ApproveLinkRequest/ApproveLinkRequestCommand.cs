using MediatR;

namespace ChatNET.API.Devices.Linking.ApproveLinkRequest;

// The secrets of the person sealed to the temporary key of the request (base64url). The server relays them and cannot open them.
public record ApproveLinkRequestCommand(Guid UserId, Guid RequestId, string? Payload) : IRequest<Unit>;
