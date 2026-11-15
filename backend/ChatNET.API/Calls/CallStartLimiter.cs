using ChatNET.API.RateLimiting;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Calls;

// Limits how many calls one person starts: a call rings someone else's devices and may push to their phone, so it is the one action here
// that disturbs another person on demand. Counted per person, in Redis with the other limits, so every API instance applies the same one.
// Starting is a hub method, which the limits of the request pipeline do not see, so the service asks here. Redis being unreachable lets
// the start through, like every other limit (the store reports it and carries on).
public sealed class CallStartLimiter
{
    private readonly ISlidingWindowStore _store;
    private readonly IOptionsMonitor<RateLimitPolicyOptions> _limits;
    private readonly string _keyPrefix;

    public CallStartLimiter(
        ISlidingWindowStore store, IOptionsMonitor<RateLimitPolicyOptions> limits, IOptions<RateLimitStoreOptions> storeOptions)
    {
        _store = store;
        _limits = limits;
        _keyPrefix = storeOptions.Value.KeyPrefix;
    }

    public async Task<bool> MayStartAsync(Guid userId, CancellationToken cancellationToken)
    {
        var limits = _limits.Get(RateLimitPolicies.StartCall);

        var decision = await _store.TryAcquireAsync(
            $"{_keyPrefix}:{RateLimitPolicies.StartCall}:user:{userId}",
            limits.PermitLimit,
            TimeSpan.FromSeconds(limits.WindowSeconds),
            cancellationToken);

        return decision.Allowed;
    }
}
