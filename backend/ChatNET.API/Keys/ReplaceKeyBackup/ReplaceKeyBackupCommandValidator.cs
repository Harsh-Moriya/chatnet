using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Keys.ReplaceKeyBackup;

public class ReplaceKeyBackupCommandValidator : AbstractValidator<ReplaceKeyBackupCommand>
{
    public ReplaceKeyBackupCommandValidator(IOptions<EncryptionOptions> options)
    {
        var maxBackupBytes = options.Value.MaxBackupBytes;

        RuleFor(x => x.Blob)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, maxBackupBytes) is { } bytes && KeyFormats.IsBackup(bytes, maxBackupBytes))
            .WithMessage("The backup is not a valid encrypted backup.");

        RuleFor(x => x.ExpectedVersion)
            .NotNull()
            .GreaterThanOrEqualTo(1);
    }
}
