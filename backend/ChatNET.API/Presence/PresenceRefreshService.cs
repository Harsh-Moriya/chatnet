using Microsoft.Extensions.Options;

namespace ChatNET.API.Presence;

// Runs in the background on every server. Each tick it renews the entries of the connections this
// server holds (so they do not expire while their owners are still there) and takes offline anyone
// whose entries expired without being settled (a server that died). The renewing is what makes a
// short expiry safe: an entry only expires if no server is vouching for it any more.
public sealed class PresenceRefreshService : BackgroundService
{
    private readonly PresenceService _presence;
    private readonly PresenceOptions _options;
    private readonly ILogger<PresenceRefreshService> _logger;

    public PresenceRefreshService(
        PresenceService presence, IOptions<PresenceOptions> options, ILogger<PresenceRefreshService> logger)
    {
        _presence = presence;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A PeriodicTimer waits for the interval and then ticks; a tick that is still being handled when
        // the next is due is not run twice at once, it is simply late.
        using var timer = new PeriodicTimer(_options.RefreshInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    // One round of refreshing and sweeping. Public so a test can run a round when it chooses instead of
    // waiting for the timer. A failure (Redis down) is logged and the loop carries on: an exception that
    // escaped a background service would stop the whole application.
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _presence.RefreshAsync(cancellationToken);
            await _presence.SweepAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A presence refresh failed; the next one will try again");
        }
    }
}
