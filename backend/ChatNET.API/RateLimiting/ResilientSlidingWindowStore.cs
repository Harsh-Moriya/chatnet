using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatNET.API.RateLimiting;

// Puts the policy for "Redis cannot be reached" around a store: the limit is skipped, the request goes ahead, and the
// person is not made to wait for a store that is down. Redis already shows in /health, and the account lockout, which is in
// Postgres, still protects passwords, so an outage of Redis does not also stop people from signing in and sending.
public sealed class ResilientSlidingWindowStore : ISlidingWindowStore
{
    private readonly ISlidingWindowStore _inner;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pause;
    private readonly ILogger<ResilientSlidingWindowStore> _logger;
    private readonly TimeProvider _clock;

    // The moment, in UtcTicks, before which Redis is not asked. 0 when it is being asked.
    private long _pausedUntil;

    // 1 from the first failure until the next answer, so an outage is one warning and its end one line however many
    // requests it lasts.
    private int _degraded;

    public ResilientSlidingWindowStore(
        ISlidingWindowStore inner,
        IOptions<RateLimitStoreOptions> options,
        ILogger<ResilientSlidingWindowStore> logger,
        TimeProvider clock)
    {
        _inner = inner;
        _timeout = TimeSpan.FromMilliseconds(options.Value.TimeoutMilliseconds);
        _pause = TimeSpan.FromSeconds(options.Value.PauseSeconds);
        _logger = logger;
        _clock = clock;
    }

    public async Task<RateLimitDecision> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        if (_clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _pausedUntil))
            return new RateLimitDecision(true, StoreUnavailable: true);

        try
        {
            var decision = await _inner.TryAcquireAsync(key, limit, window, cancellationToken).WaitAsync(_timeout, cancellationToken);

            if (Interlocked.Exchange(ref _degraded, 0) == 1)
                _logger.LogInformation("Rate limits are counted in Redis again");

            return decision;
        }
        // A person who gave up is not an outage, and anything that is not Redis or the wait for it is a mistake in the code
        // that must show.
        catch (Exception failure) when (failure is RedisException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _pausedUntil, (_clock.GetUtcNow() + _pause).UtcTicks);

            // The kind of failure and nothing it says: its text can hold an address, and the key holds who was limited.
            if (Interlocked.Exchange(ref _degraded, 1) == 0)
                _logger.LogWarning(
                    "Rate limits are not applied while Redis cannot be reached ({FailureType}); requests go ahead until it answers",
                    failure.GetType().Name);

            return new RateLimitDecision(true, StoreUnavailable: true);
        }
    }
}
