using ChatNET.API.Auth.Models;
using MediatR;

namespace ChatNET.API.Auth.Login;

public record LoginCommand(
    string Email,
    string Password
) : IRequest<AuthResponse>;
