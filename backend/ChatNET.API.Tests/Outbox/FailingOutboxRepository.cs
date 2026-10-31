using ChatNET.API.Outbox.Models;
using ChatNET.API.Outbox.Persistence;

namespace ChatNET.API.Tests.Outbox;

// Test double for an outbox table that cannot be read: the claim throws what it is told to. The rest is never reached.
public sealed class FailingOutboxRepository : IOutboxRepository
{
    private readonly Func<Exception> _failure;

    public FailingOutboxRepository(Func<Exception> failure)
    {
        _failure = failure;
    }

    public Task<List<OutboxMessage>> ClaimDueAsync(
        DateTime now, int batchSize, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken) =>
        throw _failure();

    public Task MarkDispatchedAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken cancellationToken) =>
        throw _failure();

    public Task RecordFailureAsync(Guid id, string error, CancellationToken cancellationToken) => throw _failure();

    public Task<int> DeleteFinishedAsync(DateTime before, int maxAttempts, CancellationToken cancellationToken) =>
        throw _failure();
}
