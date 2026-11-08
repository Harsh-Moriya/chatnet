using ChatNET.API.Keys;
using FluentValidation;

namespace ChatNET.API.Devices.Linking.ApproveLinkRequest;

public class ApproveLinkRequestCommandValidator : AbstractValidator<ApproveLinkRequestCommand>
{
    // A sealed box is 62 bytes longer than what it holds, which is the secrets of one person: a few hundred bytes for each
    // generation of the account key.
    private const int SealedOverhead = 62;
    public const int MaxPayloadBytes = 64 * 1024;

    public ApproveLinkRequestCommandValidator()
    {
        RuleFor(x => x.Payload)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, MaxPayloadBytes) is { } bytes && IsSealedBox(bytes))
            .WithMessage("The payload is not a sealed box.");
    }

    private static bool IsSealedBox(byte[] bytes) =>
        bytes.Length > SealedOverhead && bytes[0] == KeyFormats.Version && bytes[1] == KeyFormats.Suite;
}
