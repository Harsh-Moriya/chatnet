using ChatNET.API.Outbox.Persistence;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Outbox;

// Does the jobs that the requests did not do: a push that failed, files that were not removed, or a process that
// stopped between the save and the job. Each pass takes the rows that are due (see IOutboxRepository.ClaimDueAsync), pushes them oldest first and
// marks the ones that worked. A row that fails is tried again later with a growing wait, up to MaxAttempts, then
// left in the table with its last error.
//
// Every server instance runs it. They never push the same row at once: a row a relay has taken is held until its
// retry is due, and the claim skips rows another relay is taking. Delivery is therefore at least once: a crash after
// a push and before its mark means the row is pushed again, which clients merge away because they merge by id.
//
// The same loop removes finished rows now and then, so the table stays the size of a day's pushes.
public sealed class OutboxRelayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxRelayService> _logger;
    private long _nextCleanupTicks;

    public OutboxRelayService(
        IServiceScopeFactory scopes, TimeProvider time, IOptions<OutboxOptions> options,
        ILogger<OutboxRelayService> logger)
    {
        _scopes = scopes;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.RelayInterval, _time);

        // The first pass comes after one interval, not at once: a row left by a server that stopped is picked up a few
        // seconds later, and a server that is still starting is not asked to push yet.
        while (await WaitForNextTickAsync(timer, stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    // One pass. Public so a test (or an operator's tool) can run it without waiting for the timer.
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

            var now = _time.GetUtcNow().UtcDateTime;
            var due = await outbox.ClaimDueAsync(
                now, _options.BatchSize, _options.MaxAttempts, _options.RetryDelay, cancellationToken);

            foreach (var row in due)
            {
                try
                {
                    await dispatcher.PushAsync(row, cancellationToken);
                    await outbox.MarkDispatchedAsync([row.Id], _time.GetUtcNow().UtcDateTime, cancellationToken);

                    _logger.LogInformation(
                        "Relay pushed {Kind} (outbox {OutboxId}) on attempt {Attempts}",
                        row.Kind, row.Id, row.Attempts);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await RecordFailureAsync(outbox, row.Id, row.Kind, row.Attempts, ex);
                }
            }

            await CleanUpWhenDueAsync(outbox, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not worth stopping the application for, and the next pass tries again. A database that is down is
            // reported by the health check.
            _logger.LogWarning(ex, "An outbox pass failed; will try again at the next one");
        }
    }

    private async Task RecordFailureAsync(IOutboxRepository outbox, Guid id, string kind, int attempts, Exception ex)
    {
        if (attempts >= _options.MaxAttempts)
        {
            _logger.LogWarning(ex,
                "Gave up pushing {Kind} (outbox {OutboxId}) after {Attempts} attempts; the row stays in the table with its error",
                kind, id, attempts);
        }
        else
        {
            _logger.LogWarning(ex,
                "Could not push {Kind} (outbox {OutboxId}) on attempt {Attempts}; it will be tried again",
                kind, id, attempts);
        }

        try
        {
            await outbox.RecordFailureAsync(id, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
        }
        catch (Exception recordFailure)
        {
            _logger.LogWarning(recordFailure, "Could not record the failure of outbox {OutboxId}", id);
        }
    }

    private async Task CleanUpWhenDueAsync(IOutboxRepository outbox, DateTime now, CancellationToken cancellationToken)
    {
        if (now.Ticks < Interlocked.Read(ref _nextCleanupTicks))
            return;

        Interlocked.Exchange(ref _nextCleanupTicks, (now + _options.CleanupInterval).Ticks);

        var removed = await outbox.DeleteFinishedAsync(now - _options.Retention, _options.MaxAttempts, cancellationToken);
        if (removed > 0)
            _logger.LogInformation("Removed {RemovedCount} finished outbox rows", removed);
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
