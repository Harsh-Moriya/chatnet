using System.Buffers.Text;
using ChatNET.API.Push.Persistence;
using FluentValidation;

namespace ChatNET.API.Push.SavePushSubscription;

public class SavePushSubscriptionCommandValidator : AbstractValidator<SavePushSubscriptionCommand>
{
    // An uncompressed P-256 point: 0x04, then the two 32 byte coordinates.
    private const int PublicKeyBytes = 65;

    private const int AuthSecretBytes = 16;

    public SavePushSubscriptionCommandValidator(PushEndpointPolicy endpoints)
    {
        // The messages never repeat the value: an endpoint is a capability (see PushSubscription).
        RuleFor(x => x.Endpoint)
            .NotEmpty()
            .MaximumLength(PushSubscriptionConfiguration.MaxEndpointLength)
            .Must(endpoint => endpoint is not null && endpoints.IsAllowed(endpoint))
            .WithMessage("The push address must be an https address of a push service this server allows.");

        RuleFor(x => x.P256dh)
            .NotEmpty()
            .Must(key => Decoded(key, PublicKeyBytes)?[0] == 0x04)
            .WithMessage("The public key is not a valid push key.");

        RuleFor(x => x.Auth)
            .NotEmpty()
            .Must(secret => Decoded(secret, AuthSecretBytes) is not null)
            .WithMessage("The authentication secret is not valid.");
    }

    // The bytes of a base64url value (the browser leaves the padding off), or null when it is not one or is not this long.
    private static byte[]? Decoded(string? value, int length)
    {
        // Checked first: decoding a value that is not base64url throws, and a request must not be able to make it.
        if (value is null || !Base64Url.IsValid(value, out var decodedLength) || decodedLength != length)
            return null;

        return Base64Url.DecodeFromChars(value);
    }
}
