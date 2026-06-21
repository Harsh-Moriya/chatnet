using ChatNET.API.Auth.Models;
using MediatR;

namespace ChatNET.API.Auth.Register;

public record RegisterCommand(
    string UserName,
    string DisplayName,
    string Email,
    string Password
) : IRequest<AuthResponse>;
