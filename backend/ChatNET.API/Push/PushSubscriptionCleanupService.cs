using ChatNET.API.Push.Persistence;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Push;

// Removes the registrations of browsers that are not used any more, once a day. A browser keeps its registration alive by loading the
// page (which registers again) or by being pushed to; one that has done neither for Push:SubscriptionRetention belongs to a browser
// nobody opens, and a push service would one day answer for it that it is gone. Removing them here keeps the table, and the work of
// finding the people who are away, from growing with every browser that was ever used.
//
// Every server instance runs it. That is harmless: the delete is a single statement that removes whatever is old enough, so a second
// instance finds nothing left to do.
public sealed class PushSubscriptionCleanupService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly PushOptions _options;
    private readonly ILogger<PushSubscriptionCleanupService> _logger;

    public PushSubscriptionCleanupService(
        IServiceScopeFactory scopes, TimeProvider time, IOptions<PushOptions> options,
        ILogger<PushSubscriptionCleanupService> logger)
    {
        _scopes = scopes;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);

        do
        {
            await CleanUpOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    public async Task CleanUpOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var subscriptions = scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>();

            var removed = await subscriptions.RemoveUnusedSinceAsync(
                _time.GetUtcNow().UtcDateTime - _options.SubscriptionRetention, cancellationToken);

            if (removed > 0)
                _logger.LogInformation("Removed {RemovedCount} push registrations that were not used", removed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not worth stopping the application for, and the next pass tries again. A database that is down is reported by the
            // health check.
            _logger.LogWarning(exception, "Removing unused push registrations failed; will try again at the next pass");
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
