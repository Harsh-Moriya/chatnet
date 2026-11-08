using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Keys.SetUpKeys;

public class SetUpKeysCommandValidator : AbstractValidator<SetUpKeysCommand>
{
    public SetUpKeysCommandValidator(IOptions<EncryptionOptions> options)
    {
        var maxBackupBytes = options.Value.MaxBackupBytes;

        // The messages never repeat the value: it is key material.
        RuleFor(x => x.Bundle)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, KeyFormats.BundleBytes) is { } bytes && KeyFormats.IsBundle(bytes, out var generation) && generation == 1)
            .WithMessage("The bundle must be a version 1 bundle for generation 1.");

        RuleFor(x => x.Backup)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, maxBackupBytes) is { } bytes && KeyFormats.IsBackup(bytes, maxBackupBytes))
            .WithMessage("The backup is not a valid encrypted backup.");
    }
}
