using MediatR;

namespace ChatNET.API.Auth.ResetPassword;

public record ResetPasswordCommand(string Token, string NewPassword) : IRequest<Unit>;
