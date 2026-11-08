using ChatNET.API.Auth.Models;
using MediatR;

namespace ChatNET.API.Auth.Login;

public record LoginCommand(
    string Email,
    string Password,
    // From the X-Device-Id header: the endpoint sets it, whatever the body says.
    Guid? DeviceId = null
) : IRequest<AuthResult>;
