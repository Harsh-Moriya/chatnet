using ChatNET.API.Auth.Models;
using MediatR;

namespace ChatNET.API.Auth.Refresh;

// The token comes from the cookie, never from the body, so a script that does not hold the cookie cannot
// supply one. Null when the request had no cookie.
//
// DeviceId is the device the browser says it is (the X-Device-Id header), used to tie the session to it.
public record RefreshCommand(string? RefreshToken, Guid? DeviceId = null) : IRequest<AuthResult>;
