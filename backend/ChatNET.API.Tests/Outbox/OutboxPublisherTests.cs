using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Outbox.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Outbox;

// What a request does with its row, without a database: the row it makes, and what it does after the push worked or
// failed.
public class OutboxPublisherTests
{

    [Fact]
    public void A_new_row_is_stamped_with_the_clock_and_left_to_its_request_for_the_claim_delay()
    {
        var clock = new ManualTimeProvider();
        var publisher = PublisherFor(clock, new RecordingOutboxRepository(), new CapturingLoggerProvider());
        var created = new MessageCreated(Guid.NewGuid(), Guid.NewGuid());

        var row = publisher.Create(created);

        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(MessageCreated.Name, row.Kind);
        Assert.Equal(created, OutboxEvents.Read(row.Kind, row.Payload));
        Assert.Equal(TimeSpan.FromSeconds(7), row.NextAttemptAt - row.CreatedAt);
        Assert.InRange((row.CreatedAt - clock.GetUtcNow().UtcDateTime).TotalMilliseconds, -1, 0);
        Assert.Null(row.DispatchedAt);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task A_push_that_worked_marks_the_row_done()
    {
        var repository = new RecordingOutboxRepository();
        var publisher = PublisherFor(new ManualTimeProvider(), repository, new CapturingLoggerProvider());
        var row = publisher.Create(new MessageCreated(Guid.NewGuid(), Guid.NewGuid()));
        var pushed = false;

        await publisher.DeliverAsync(row, () =>
        {
            pushed = true;
            return Task.CompletedTask;
        });

        Assert.True(pushed);
        Assert.Equal([row.Id], repository.Marked);
    }

    [Fact]
    public async Task A_push_that_failed_leaves_the_row_and_says_so_with_ids_only()
    {
        var repository = new RecordingOutboxRepository();
        var capture = new CapturingLoggerProvider();
        var publisher = PublisherFor(new ManualTimeProvider(), repository, capture);
        var row = publisher.Create(new MessageCreated(Guid.NewGuid(), Guid.NewGuid()));

        await publisher.DeliverAsync(row, () => throw new InvalidOperationException("Redis is down"));

        Assert.Empty(repository.Marked);
        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]"));
        Assert.Contains(row.Id.ToString(), warning);
        Assert.Contains(MessageCreated.Name, warning);
    }

    [Fact]
    public async Task A_row_that_cannot_be_marked_is_a_warning_not_an_error_for_the_request()
    {
        var repository = new RecordingOutboxRepository { FailToMark = true };
        var capture = new CapturingLoggerProvider();
        var publisher = PublisherFor(new ManualTimeProvider(), repository, capture);
        var row = publisher.Create(new MessageCreated(Guid.NewGuid(), Guid.NewGuid()));

        await publisher.DeliverAsync(row, () => Task.CompletedTask);

        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]"));
        Assert.Contains(row.Id.ToString(), warning);
    }

    [Fact]
    public async Task A_request_that_is_cancelled_during_the_push_is_not_hidden()
    {
        var repository = new RecordingOutboxRepository();
        var publisher = PublisherFor(new ManualTimeProvider(), repository, new CapturingLoggerProvider());
        var row = publisher.Create(new MessageCreated(Guid.NewGuid(), Guid.NewGuid()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => publisher.DeliverAsync(row, () => throw new OperationCanceledException()));

        Assert.Empty(repository.Marked);
    }

    private static OutboxPublisher PublisherFor(
        TimeProvider clock, IOutboxRepository repository, CapturingLoggerProvider capture) =>
        new(
            clock,
            Options.Create(new OutboxOptions { ClaimAfter = TimeSpan.FromSeconds(7) }),
            repository,
            new Logger<OutboxPublisher>(LoggerFactory.Create(logging => logging.AddProvider(capture))));

    private sealed class RecordingOutboxRepository : IOutboxRepository
    {
        public List<Guid> Marked { get; } = [];

        public bool FailToMark { get; init; }

        public Task MarkDispatchedAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken cancellationToken)
        {
            if (FailToMark)
                throw new InvalidOperationException("The database is away.");

            Marked.AddRange(ids);
            return Task.CompletedTask;
        }

        public Task<List<OutboxMessage>> ClaimDueAsync(
            DateTime now, int batchSize, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordFailureAsync(Guid id, string error, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteFinishedAsync(DateTime before, int maxAttempts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
