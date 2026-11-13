using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Keys.AddKeyGeneration;

public class AddKeyGenerationCommandValidator : AbstractValidator<AddKeyGenerationCommand>
{
    public AddKeyGenerationCommandValidator(IOptions<EncryptionOptions> options)
    {
        var maxBackupBytes = options.Value.MaxBackupBytes;

        // The messages never repeat the value: it is key material. Which generation is the next one is for the handler to say, since
        // it depends on what the person has; here it can only be said that a first generation is not a new one.
        RuleFor(x => x.Bundle)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, KeyFormats.BundleBytes) is { } bytes && KeyFormats.IsBundle(bytes, out var generation) && generation >= 2)
            .WithMessage("The bundle must be a version 1 bundle for generation 2 or later.");

        RuleFor(x => x.Backup)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, maxBackupBytes) is { } bytes && KeyFormats.IsBackup(bytes, maxBackupBytes))
            .WithMessage("The backup is not a valid encrypted backup.");

        RuleFor(x => x.ExpectedBackupVersion)
            .NotNull()
            .GreaterThanOrEqualTo(1);
    }
}
