using FluentValidation;

namespace ChatNET.API.Messaging.GetMessageHistory;

public class GetMessageHistoryQueryValidator : AbstractValidator<GetMessageHistoryQuery>
{
    public GetMessageHistoryQueryValidator()
    {
        // Reading upwards and downwards from a message in one call has no meaning. A cursor that is
        // not a real message (including an empty id) is refused by the handler, which is the only
        // place that can tell.
        RuleFor(x => x.After)
            .Null()
            .When(x => x.Before.HasValue)
            .WithMessage("Use either 'before' or 'after', not both.");
    }
}
