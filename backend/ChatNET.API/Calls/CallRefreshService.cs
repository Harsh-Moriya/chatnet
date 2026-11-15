using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Calls;

// Runs in the background on every instance. Each tick it ends the calls that are due (a ring that ran out, a side whose lease or grace
// ended) and finishes those whose end was begun and not completed; and every RenewInterval it renews the sides of calls whose
// connections this instance holds. The renewing is what makes a short lease safe: a side only expires if no instance is vouching for it
// any more, which is how a call whose instance died comes to an end.
public sealed class CallRefreshService : BackgroundService
{
    private readonly CallService _calls;
    private readonly CallOptions _options;
    private readonly ILogger<CallRefreshService> _logger;

    public CallRefreshService(CallService calls, IOptions<CallOptions> options, ILogger<CallRefreshService> logger)
    {
        _calls = calls;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        var sinceRenewal = Stopwatch.StartNew();

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var renew = sinceRenewal.Elapsed >= _options.RenewInterval;
            if (renew)
                sinceRenewal.Restart();

            await RunAsync(renew, stoppingToken);
        }
    }

    // One round of renewing and sweeping. Public so a test can run a round when it chooses instead of waiting for the timer. A failure
    // (Redis down) is logged and the loop carries on: an exception that escaped a background service would stop the whole application.
    public Task RunOnceAsync(CancellationToken cancellationToken) => RunAsync(renew: true, cancellationToken);

    private async Task RunAsync(bool renew, CancellationToken cancellationToken)
    {
        try
        {
            if (renew)
                await _calls.RenewHeldAsync(cancellationToken);

            await _calls.SweepAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A round of looking after calls failed; the next one will try again");
        }
    }
}
