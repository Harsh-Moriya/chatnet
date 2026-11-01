using FluentValidation;

namespace ChatNET.API.Media.Avatars;

public class CreateAvatarUploadCommandValidator : AbstractValidator<CreateAvatarUploadCommand>
{
    public CreateAvatarUploadCommandValidator()
    {
        // Two rules, because a condition at the end of a chain applies to all of it: with the type missing, the one rule
        // would be skipped altogether and nothing would refuse the request.
        RuleFor(x => x.ContentType)
            .NotEmpty();

        RuleFor(x => x.ContentType)
            .Must(type => AvatarRules.Match(type!) is not null)
            .WithMessage("A picture must be a PNG, JPEG, WebP or GIF image.")
            .When(x => !string.IsNullOrWhiteSpace(x.ContentType));

        RuleFor(x => x.SizeBytes)
            .GreaterThan(0)
            .WithMessage("The picture is empty.");

        RuleFor(x => x.SizeBytes)
            .LessThanOrEqualTo(AvatarRules.MaxBytes)
            .WithMessage("The picture is larger than the limit.");
    }
}

public class SetAvatarCommandValidator : AbstractValidator<SetAvatarCommand>
{
    // Far longer than any key the server makes; it only stops an absurd value from being compared.
    private const int MaxKeyLength = 300;

    public SetAvatarCommandValidator()
    {
        RuleFor(x => x.UploadKey)
            .NotEmpty()
            .WithMessage("Say which upload is the picture.")
            .MaximumLength(MaxKeyLength);
    }
}
