using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Push.Persistence;
using MediatR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Push.SavePushSubscription;

public class SavePushSubscriptionHandler : IRequestHandler<SavePushSubscriptionCommand, Unit>
{
    private readonly IPushSubscriptionRepository _subscriptions;
    private readonly PushOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SavePushSubscriptionHandler> _logger;

    public SavePushSubscriptionHandler(
        IPushSubscriptionRepository subscriptions, IOptions<PushOptions> options, TimeProvider time,
        ILogger<SavePushSubscriptionHandler> logger)
    {
        _subscriptions = subscriptions;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<Unit> Handle(SavePushSubscriptionCommand request, CancellationToken cancellationToken)
    {
        // A page that shows the switch asked for the key first and found there is none; a registration anyway is refused the
        // same way, so nothing is kept that no push will ever use.
        if (!_options.Enabled)
            throw new NotFoundException(PushMessages.NotSetUp);

        await _subscriptions.SaveAsync(
            request.UserId, request.Endpoint!, request.P256dh!, request.Auth!, _options.MaxSubscriptionsPerUser,
            _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds(), cancellationToken);

        // Never the endpoint, which is a capability.
        _logger.LogInformation("User {UserId} registered a browser for push notifications", request.UserId);
        return Unit.Value;
    }
}
