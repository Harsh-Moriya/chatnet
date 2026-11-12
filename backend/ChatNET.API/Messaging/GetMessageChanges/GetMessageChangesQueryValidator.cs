using FluentValidation;

namespace ChatNET.API.Messaging.GetMessageChanges;

public class GetMessageChangesQueryValidator : AbstractValidator<GetMessageChangesQuery>
{
    public const string SinceRequired = "Say where to read from with 'since', the number of the last change you have seen, or 0.";
    public const string SinceNegative = "'since' cannot be negative.";

    public GetMessageChangesQueryValidator()
    {
        RuleFor(x => x.Since)
            .NotNull()
            .WithMessage(SinceRequired);

        RuleFor(x => x.Since)
            .GreaterThanOrEqualTo(0L)
            .When(x => x.Since.HasValue)
            .WithMessage(SinceNegative);
    }
}
