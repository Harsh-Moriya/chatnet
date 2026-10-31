using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Outbox.Persistence;

// Reading and finishing outbox rows. Writing them is done by the repositories that save the change a row belongs to,
// in the same SaveChanges.
public interface IOutboxRepository
{
    // Takes up to batchSize rows that are due (not pushed, not given up on, past their NextAttemptAt), oldest first,
    // and holds them for the delay of their attempt (retryDelay, doubling), so no other relay takes them meanwhile.
    // One statement, and rows another relay is taking at this moment are skipped, not waited for. The returned rows
    // carry the new attempt count.
    Task<List<OutboxMessage>> ClaimDueAsync(
        DateTime now, int batchSize, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken);

    // Marks rows pushed. A row that already is stays as it was.
    Task MarkDispatchedAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken cancellationToken);

    Task RecordFailureAsync(Guid id, string error, CancellationToken cancellationToken);

    // Removes the rows that were pushed before the time, and the rows that were given up on (maxAttempts reached,
    // never pushed) created before it. Returns how many.
    Task<int> DeleteFinishedAsync(DateTime before, int maxAttempts, CancellationToken cancellationToken);
}
