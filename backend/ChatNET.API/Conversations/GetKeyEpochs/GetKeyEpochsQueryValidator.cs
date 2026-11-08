using FluentValidation;

namespace ChatNET.API.Conversations.GetKeyEpochs;

public class GetKeyEpochsQueryValidator : AbstractValidator<GetKeyEpochsQuery>
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public GetKeyEpochsQueryValidator()
    {
        RuleFor(x => x.From).GreaterThanOrEqualTo(1).WithMessage("The first epoch to return must be at least 1.");
        RuleFor(x => x.Limit).InclusiveBetween(1, MaxLimit).WithMessage($"The limit must be between 1 and {MaxLimit}.");
    }
}
