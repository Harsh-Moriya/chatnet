using System.ComponentModel.DataAnnotations;

namespace ChatNET.API.RateLimiting;

// How the shared counters are kept. Bound from the "RateLimiting:Store" configuration section and validated when the host
// starts. The limits themselves are RateLimitPolicyOptions, one section per policy.
public sealed class RateLimitStoreOptions
{
    public const string SectionName = "RateLimiting:Store";

    // In front of every key in Redis, so two environments that share one Redis (or two test hosts) do not count each other.
    [Required]
    [RegularExpression(@"^[A-Za-z0-9_.-]+$")]
    public string KeyPrefix { get; set; } = "ratelimit";

    // How long Redis may take to answer before the limit is skipped for that request. The limiter sits in front of every
    // limited request, so it must not wait the five seconds that Redis' client does by default.
    [Range(10, 10_000)]
    public int TimeoutMilliseconds { get; set; } = 200;

    // After a failed call, how long Redis is left alone before the next request tries it again. Without it every request of an
    // outage would wait out the timeout.
    [Range(1, 600)]
    public int PauseSeconds { get; set; } = 5;
}
