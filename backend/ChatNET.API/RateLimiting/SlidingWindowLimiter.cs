using System.Diagnostics;
using System.Threading.RateLimiting;

namespace ChatNET.API.RateLimiting;

// The limiter of one key, in the shape ASP.NET Core's rate limiting middleware asks for. It holds no count: the count is in the
// store, shared by every server, so this object can be made again at any moment without losing anything.
internal sealed class SlidingWindowLimiter : RateLimiter
{
    private readonly ISlidingWindowStore _store;
    private readonly string _key;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private long _lastUsed = Stopwatch.GetTimestamp();

    public SlidingWindowLimiter(ISlidingWindowStore store, string key, int limit, TimeSpan window)
    {
        _store = store;
        _key = key;
        _limit = limit;
        _window = window;
    }

    // The middleware drops a limiter that has been idle for a while, which is what keeps one per person who ever made a request
    // from staying in memory.
    public override TimeSpan? IdleDuration => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUsed));

    public override RateLimiterStatistics? GetStatistics() => null;

    // The middleware tries this synchronous way first and asks with AcquireAsync when it does not acquire. A round trip to
    // Redis must not block a thread, so this one never decides: it says "not yet" and the answer comes from AcquireAsync.
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Lease.NotDecided;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        if (permitCount != 1)
            throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, "A request takes one place in the window.");

        Interlocked.Exchange(ref _lastUsed, Stopwatch.GetTimestamp());

        var decision = await _store.TryAcquireAsync(_key, _limit, _window, cancellationToken);
        return decision.Allowed ? Lease.Acquired : new Lease(isAcquired: false, decision.RetryAfter);
    }

    private sealed class Lease : RateLimitLease
    {
        public static readonly Lease Acquired = new(isAcquired: true, retryAfter: null);
        public static readonly Lease NotDecided = new(isAcquired: false, retryAfter: null);

        private readonly TimeSpan? _retryAfter;

        public Lease(bool isAcquired, TimeSpan? retryAfter)
        {
            IsAcquired = isAcquired;
            _retryAfter = retryAfter;
        }

        public override bool IsAcquired { get; }

        public override IEnumerable<string> MetadataNames => _retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        // How long until a place is free, which the rejection turns into a Retry-After header.
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retryAfter is { } retryAfter && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
