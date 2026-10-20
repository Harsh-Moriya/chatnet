using MediatR;

namespace ChatNET.API.Auth.Logout;

// Null when the request had no cookie: there is nothing to revoke, and signing out is still a success.
public record LogoutCommand(string? RefreshToken) : IRequest<Unit>;
