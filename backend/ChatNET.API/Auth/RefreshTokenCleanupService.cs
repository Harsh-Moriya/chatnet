using ChatNET.API.Auth.Persistence;

namespace ChatNET.API.Auth;

// Removes refresh tokens that expired a while ago, once a day. A token is useless the moment it expires,
// so this only keeps the table from growing for ever. They are kept for a week past their expiry, which
// leaves time to see what happened to a session when someone reports a problem.
//
// Every server instance runs it. That is harmless: the delete is a single statement that removes
// whatever is old enough, so a second instance finds nothing left to do.
public sealed class RefreshTokenCleanupService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<RefreshTokenCleanupService> _logger;

    public RefreshTokenCleanupService(
        IServiceScopeFactory scopes, TimeProvider time, ILogger<RefreshTokenCleanupService> logger)
    {
        _scopes = scopes;
        _time = time;
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
            var tokens = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();

            var removed = await tokens.DeleteExpiredBeforeAsync(
                _time.GetUtcNow().UtcDateTime - Retention, cancellationToken);

            if (removed > 0)
                _logger.LogInformation("Removed {RemovedCount} expired refresh tokens", removed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not worth stopping the application for, and the next pass tries again. A database that is
            // down is reported by the health check.
            _logger.LogWarning(exception, "Removing expired refresh tokens failed; will try again at the next pass");
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
