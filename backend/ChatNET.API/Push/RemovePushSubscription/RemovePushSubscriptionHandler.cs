using ChatNET.API.Push.Persistence;
using MediatR;

namespace ChatNET.API.Push.RemovePushSubscription;

public class RemovePushSubscriptionHandler : IRequestHandler<RemovePushSubscriptionCommand, Unit>
{
    private readonly IPushSubscriptionRepository _subscriptions;
    private readonly ILogger<RemovePushSubscriptionHandler> _logger;

    public RemovePushSubscriptionHandler(
        IPushSubscriptionRepository subscriptions, ILogger<RemovePushSubscriptionHandler> logger)
    {
        _subscriptions = subscriptions;
        _logger = logger;
    }

    // Always succeeds: a browser that signs out or turns the switch off has nothing to learn from "it was not registered",
    // and the answer must not say whether the browser of another person is.
    public async Task<Unit> Handle(RemovePushSubscriptionCommand request, CancellationToken cancellationToken)
    {
        if (await _subscriptions.RemoveAsync(request.UserId, request.Endpoint!, cancellationToken))
            _logger.LogInformation("User {UserId} removed a browser from push notifications", request.UserId);

        return Unit.Value;
    }
}
