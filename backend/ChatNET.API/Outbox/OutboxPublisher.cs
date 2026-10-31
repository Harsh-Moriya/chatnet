using ChatNET.API.Common.Time;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Outbox.Persistence;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Outbox;

// What a handler uses to make a push reliable. It first makes the row (Create), which is saved together with the
// change by the repository that saves the change; after the commit it makes the push itself (Deliver) and, if that
// worked, marks the row so the relay leaves it alone.
public class OutboxPublisher
{
    private readonly TimeProvider _time;
    private readonly OutboxOptions _options;
    private readonly IOutboxRepository _outbox;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        TimeProvider time, IOptions<OutboxOptions> options, IOutboxRepository outbox, ILogger<OutboxPublisher> logger)
    {
        _time = time;
        _options = options.Value;
        _outbox = outbox;
        _logger = logger;
    }

    // A row that is not saved yet. The relay may take it only after ClaimAfter, so the request that made it has that
    // long to push without anyone else pushing the same thing.
    public OutboxMessage Create(IOutboxEvent outboxEvent)
    {
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Kind = outboxEvent.Kind,
            Payload = OutboxEvents.Serialize(outboxEvent),
            CreatedAt = now,
            NextAttemptAt = now + _options.ClaimAfter,
        };
    }

    // Only after the change is committed, so nobody is told about something that was rolled back. The change is
    // stored by then, so a failing push (Redis down, for example) must not turn a successful request into an error:
    // the row stays and the relay pushes it. A failure to mark the row has the same cost, one repeated push, which a
    // client that already has the data merges away.
    //
    // A cancellation is a failed push too, unless it is the request's own (the client left, the host is stopping): a
    // timeout can surface as a cancellation, and must not fail a request whose change is stored.
    public async Task DeliverAsync(OutboxMessage row, Func<Task> push, CancellationToken cancellationToken)
    {
        try
        {
            await push();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Could not push {Kind} (outbox {OutboxId}); the relay will retry it",
                row.Kind, row.Id);

            return;
        }

        try
        {
            // Not tied to the request: a request that ends now has already done its push.
            await _outbox.MarkDispatchedAsync([row.Id], _time.GetUtcNow().UtcDateTime, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Pushed {Kind} (outbox {OutboxId}) but could not mark it done; the relay will push it once more",
                row.Kind, row.Id);
        }
    }
}
