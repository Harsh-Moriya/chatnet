using System.Security.Cryptography;
using System.Text;
using ChatNET.API.Auth.Services;
using ChatNET.API.RateLimiting;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.PasswordReset;

// Limits how many reset emails one address is sent, however many addresses or clients ask: without it, anyone could fill a stranger's
// inbox from a few clients. The count lives in Redis with the other limits, so every API instance applies the same one.
//
// The address is in the request body, which the rate limiter in the pipeline has not read, so the handler asks here. The key holds
// a keyed hash of the address (HMAC with a key derived from the signing key), never the address, and not a plain hash either: a
// plain hash of an email address can be undone by trying a list of addresses. Redis being unreachable lets the request through, like
// every other limit (the store reports it and carries on).
public sealed class PasswordResetEmailLimiter
{
    private static readonly byte[] KeyPurpose = Encoding.UTF8.GetBytes("chatnet:password-reset-email:v1");

    private readonly ISlidingWindowStore _store;
    private readonly IOptionsMonitor<RateLimitPolicyOptions> _limits;
    private readonly string _keyPrefix;
    private readonly byte[] _hashKey;

    public PasswordResetEmailLimiter(
        ISlidingWindowStore store,
        IOptionsMonitor<RateLimitPolicyOptions> limits,
        IOptions<RateLimitStoreOptions> storeOptions,
        IOptions<JwtOptions> jwt)
    {
        _store = store;
        _limits = limits;
        _keyPrefix = storeOptions.Value.KeyPrefix;

        // One key for this one purpose, made from the signing key, so the signing key itself is never used as an HMAC key here.
        _hashKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt.Value.SigningKey), KeyPurpose);
    }

    // True when another email may be sent to this address. The address is the normalized form, so spelling it in another case
    // is the same address.
    public async Task<bool> MaySendAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        var limits = _limits.Get(RateLimitPolicies.PasswordResetEmail);
        var who = Convert.ToHexStringLower(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(normalizedEmail)));

        var decision = await _store.TryAcquireAsync(
            $"{_keyPrefix}:{RateLimitPolicies.PasswordResetEmail}:email:{who}",
            limits.PermitLimit,
            TimeSpan.FromSeconds(limits.WindowSeconds),
            cancellationToken);

        return decision.Allowed;
    }
}
