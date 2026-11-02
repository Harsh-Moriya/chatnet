using FluentValidation;

namespace ChatNET.API.Messaging.GetMessageHistory;

public class GetMessageHistoryQueryValidator : AbstractValidator<GetMessageHistoryQuery>
{
    public const string OneCursor = "Use only one of 'before', 'after' and 'around'.";

    public GetMessageHistoryQueryValidator()
    {
        // Reading upwards and downwards from a message in one call has no meaning, and neither has reading around another
        // message at the same time. A cursor that is not a real message (including an empty id) is refused by the handler,
        // which is the only place that can tell.
        RuleFor(x => x.After)
            .Null()
            .When(x => x.Before.HasValue)
            .WithMessage("Use either 'before' or 'after', not both.");

        RuleFor(x => x.Around)
            .Null()
            .When(x => x.Before.HasValue || x.After.HasValue)
            .WithMessage(OneCursor);
    }
}
