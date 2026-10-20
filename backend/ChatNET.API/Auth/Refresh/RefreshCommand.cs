using ChatNET.API.Auth.Models;
using MediatR;

namespace ChatNET.API.Auth.Refresh;

// The token comes from the cookie, never from the body, so a script that does not hold the cookie cannot
// supply one. Null when the request had no cookie.
public record RefreshCommand(string? RefreshToken) : IRequest<AuthResult>;
