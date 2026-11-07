using ChatNET.API.RateLimiting;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatNET.API.Tests.RateLimiting;

// While Redis cannot be reached the limits are not applied: requests go ahead and one Warning says so. These tests
// fake the store underneath, so each way it can fail is made on purpose and the clock is moved by hand.
public class ResilientSlidingWindowStoreTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _clock = new();
    private readonly CapturingLoggerProvider _log = new();
    private readonly ScriptedStore _inner = new();

    private ResilientSlidingWindowStore NewStore(int timeoutMilliseconds = 1000, int pauseSeconds = 5) =>
        new(_inner,
            Options.Create(new RateLimitStoreOptions { TimeoutMilliseconds = timeoutMilliseconds, PauseSeconds = pauseSeconds }),
            new LoggerFactory([_log]).CreateLogger<ResilientSlidingWindowStore>(),
            _clock);

    private static Task<RateLimitDecision> AskAsync(ResilientSlidingWindowStore store, string key = "k", CancellationToken ct = default) =>
        store.TryAcquireAsync(key, 5, Window, ct);

    private IEnumerable<string> Lines(LogLevel level) => _log.Entries.Where(e => e.StartsWith($"[{level}]"));

    // ##### A store that answers #####

    [Fact]
    public async Task What_the_store_says_is_passed_on_as_it_is()
    {
        var store = NewStore();

        _inner.Next = _ => Task.FromResult(new RateLimitDecision(true));
        Assert.Equal(new RateLimitDecision(true), await AskAsync(store));

        _inner.Next = _ => Task.FromResult(new RateLimitDecision(false, TimeSpan.FromSeconds(3)));
        var refused = await AskAsync(store);

        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(3), refused.RetryAfter);
        Assert.False(refused.StoreUnavailable);
        Assert.Empty(_log.Entries);
    }

    // ##### A store that does not #####

    [Fact]
    public async Task A_failing_store_lets_the_request_go_ahead_and_says_so()
    {
        _inner.Next = _ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");

        var decision = await AskAsync(NewStore());

        Assert.True(decision.Allowed);
        Assert.True(decision.StoreUnavailable);
    }

    // A time limit on the test itself: a wait that never ends must fail, not hang the run.
    [Fact(Timeout = 5000)]
    public async Task A_store_that_does_not_answer_in_time_lets_the_request_go_ahead()
    {
        _inner.Next = _ => new TaskCompletionSource<RateLimitDecision>().Task;

        var decision = await AskAsync(NewStore(timeoutMilliseconds: 50));

        Assert.True(decision.Allowed);
        Assert.True(decision.StoreUnavailable);
    }

    [Fact]
    public async Task Redis_is_not_asked_again_until_the_pause_is_over()
    {
        var store = NewStore(pauseSeconds: 5);
        _inner.Next = _ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
        await AskAsync(store);
        Assert.Equal(1, _inner.Calls);

        // Within the pause every request goes ahead without a call, so an outage does not add a wait to each of them.
        _clock.Advance(TimeSpan.FromSeconds(4));
        for (var i = 0; i < 10; i++)
        {
            var decision = await AskAsync(store);
            Assert.True(decision.Allowed);
            Assert.True(decision.StoreUnavailable);
        }
        Assert.Equal(1, _inner.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1.5));
        _inner.Next = _ => Task.FromResult(new RateLimitDecision(false, TimeSpan.FromSeconds(2)));
        var back = await AskAsync(store);

        Assert.Equal(2, _inner.Calls);
        Assert.False(back.Allowed);
        Assert.False(back.StoreUnavailable);
    }

    [Fact]
    public async Task A_pause_that_ends_on_a_store_that_still_fails_starts_another()
    {
        var store = NewStore(pauseSeconds: 5);
        _inner.Next = _ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");

        await AskAsync(store);
        _clock.Advance(TimeSpan.FromSeconds(6));
        await AskAsync(store);
        Assert.Equal(2, _inner.Calls);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await AskAsync(store);
        Assert.Equal(2, _inner.Calls);
    }

    // ##### What is logged #####

    [Fact]
    public async Task An_outage_is_one_warning_and_the_return_is_one_line_however_many_requests_it_lasts()
    {
        var store = NewStore(pauseSeconds: 5);
        _inner.Next = _ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");

        await AskAsync(store);
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(6));
            await AskAsync(store);
        }

        Assert.Single(Lines(LogLevel.Warning));

        _inner.Next = _ => Task.FromResult(new RateLimitDecision(true));
        _clock.Advance(TimeSpan.FromSeconds(6));
        await AskAsync(store);
        await AskAsync(store);

        Assert.Single(Lines(LogLevel.Information));

        // A second outage is a new warning.
        _inner.Next = _ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
        await AskAsync(store);
        Assert.Equal(2, Lines(LogLevel.Warning).Count());
    }

    [Fact]
    public async Task The_log_has_neither_who_was_limited_nor_what_redis_said()
    {
        var store = NewStore();
        _inner.Next = _ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "password=hunter2 at 10.0.0.9:6379");

        await AskAsync(store, key: "ratelimit:send:user:5f1b7c2e");

        Assert.NotEmpty(_log.Entries);
        Assert.DoesNotContain(_log.Entries, e => e.Contains("5f1b7c2e") || e.Contains("hunter2") || e.Contains("10.0.0.9"));
    }

    // ##### What is not an outage #####

    [Fact]
    public async Task A_request_that_was_cancelled_is_not_an_outage()
    {
        var store = NewStore();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _inner.Next = ct => Task.FromCanceled<RateLimitDecision>(ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskAsync(store, ct: cancelled.Token));

        _inner.Next = _ => Task.FromResult(new RateLimitDecision(true));
        Assert.False((await AskAsync(store)).StoreUnavailable);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task A_mistake_in_the_code_is_not_hidden_as_an_outage()
    {
        var store = NewStore();
        _inner.Next = _ => throw new InvalidOperationException("a bug");

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(store));
    }

    private sealed class ScriptedStore : ISlidingWindowStore
    {
        public Func<CancellationToken, Task<RateLimitDecision>> Next { get; set; } = _ => Task.FromResult(new RateLimitDecision(true));

        public int Calls { get; private set; }

        public Task<RateLimitDecision> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
        {
            Calls++;
            return Next(cancellationToken);
        }
    }
}
