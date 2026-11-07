using ChatNET.API.Auth.Persistence;

namespace ChatNET.API.Auth;

// Removes refresh tokens and password reset tokens that expired a while ago, once a day. A token is useless the moment it
// expires, so this only keeps the tables from growing for ever. They are kept for a week past their expiry, which leaves time to
// see what happened to a session or a reset when someone reports a problem.
//
// Every server instance runs it. That is harmless: each delete is a single statement that removes whatever is old enough, so a
// second instance finds nothing left to do.
public sealed class AuthTokenCleanupService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthTokenCleanupService> _logger;

    public AuthTokenCleanupService(
        IServiceScopeFactory scopes, TimeProvider time, ILogger<AuthTokenCleanupService> logger)
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
        var expiredBefore = _time.GetUtcNow().UtcDateTime - Retention;

        // Each kind has its own attempt, so a failure in one does not leave the other to wait for tomorrow.
        await RunAsync(
            "refresh tokens",
            provider => provider.GetRequiredService<IRefreshTokenRepository>().DeleteExpiredBeforeAsync(expiredBefore, cancellationToken));
        await RunAsync(
            "password reset tokens",
            provider => provider.GetRequiredService<IPasswordResetTokenRepository>().DeleteExpiredBeforeAsync(expiredBefore, cancellationToken));
    }

    private async Task RunAsync(string what, Func<IServiceProvider, Task<int>> remove)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var removed = await remove(scope.ServiceProvider);

            if (removed > 0)
                _logger.LogInformation("Removed {RemovedCount} expired {Tokens}", removed, what);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not worth stopping the application for, and the next pass tries again. A database that is
            // down is reported by the health check.
            _logger.LogWarning(exception, "Removing expired {Tokens} failed; will try again at the next pass", what);
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
