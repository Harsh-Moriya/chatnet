using ChatNET.API.Push.Persistence;
using FluentValidation;

namespace ChatNET.API.Push.RemovePushSubscription;

public class RemovePushSubscriptionCommandValidator : AbstractValidator<RemovePushSubscriptionCommand>
{
    public RemovePushSubscriptionCommandValidator()
    {
        // Only the shape: a browser removing itself may carry an address that is no longer on the allowed list, and has to be
        // able to go.
        RuleFor(x => x.Endpoint)
            .NotEmpty()
            .MaximumLength(PushSubscriptionConfiguration.MaxEndpointLength);
    }
}
