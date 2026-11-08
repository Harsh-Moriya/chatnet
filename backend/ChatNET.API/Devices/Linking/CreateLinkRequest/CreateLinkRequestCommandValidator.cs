using ChatNET.API.Keys;
using FluentValidation;

namespace ChatNET.API.Devices.Linking.CreateLinkRequest;

public class CreateLinkRequestCommandValidator : AbstractValidator<CreateLinkRequestCommand>
{
    public CreateLinkRequestCommandValidator()
    {
        // The messages never repeat the value.
        RuleFor(x => x.TemporaryPublicKey)
            .NotEmpty()
            .Must(value => KeyFormats.Decode(value, KeyFormats.KeyBytes)?.Length == KeyFormats.KeyBytes)
            .WithMessage("The temporary public key must be 32 bytes, base64url.");

        RuleFor(x => x.Code)
            .NotEmpty()
            .Must(LinkCodes.IsCode)
            .WithMessage("The link code must be 10 characters from 0123456789ABCDEFGHJKMNPQRSTVWXYZ.");

        RuleFor(x => x.Kind).MustBeKind();
        RuleFor(x => x.Name).MustBeName();
    }
}
