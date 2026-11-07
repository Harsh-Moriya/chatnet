using FluentValidation;

namespace ChatNET.API.Auth.ResetPassword;

public class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        // The token is 32 random bytes in base64url, which is 43 characters. Anything else cannot be one, so it is refused before
        // the database is asked.
        RuleFor(x => x.Token)
            .NotEmpty()
            .Matches("^[A-Za-z0-9_-]{43}$")
            .WithMessage("This link is invalid or has expired.");

        // The same limits as registration: the upper limit stops a single request from asking the server to hash an enormous value.
        // The account rules (digits, upper and lower case) are Identity's and are reported on the same field by the handler.
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128);
    }
}
