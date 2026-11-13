using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Keys.ResetKeys;

public class ResetKeysCommandValidator : AbstractValidator<ResetKeysCommand>
{
    public ResetKeysCommandValidator(IOptions<EncryptionOptions> options)
    {
        var maxBackupBytes = options.Value.MaxBackupBytes;

        // The messages never repeat the value: it is key material.
        RuleFor(x => x.Bundle)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, KeyFormats.BundleBytes) is { } bytes && KeyFormats.IsBundle(bytes, out var generation) && generation >= 2)
            .WithMessage("The bundle must be a version 1 bundle for generation 2 or later.");

        RuleFor(x => x.Backup)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, maxBackupBytes) is { } bytes && KeyFormats.IsBackup(bytes, maxBackupBytes))
            .WithMessage("The backup is not a valid encrypted backup.");

        // The same bound as signing in, so a password that could not have been set is not hashed.
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}
