using MediatR;

namespace ChatNET.API.Auth.ForgotPassword;

public record ForgotPasswordCommand(string Email) : IRequest<Unit>;
