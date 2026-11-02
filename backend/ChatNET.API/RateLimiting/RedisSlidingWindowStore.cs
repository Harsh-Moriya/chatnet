using ChatNET.API.Presence;
using StackExchange.Redis;

namespace ChatNET.API.RateLimiting;

// A sliding window log in Redis: one sorted set per key, one member per request that went ahead, scored with the time it was made.
// Counting means dropping the members older than the window and looking at how many are left. Redis runs a script one at a
// time, so the count and the decision cannot be split by another server's request; and the time is Redis's own, so servers
// with clocks that differ still agree on the window.
public sealed class RedisSlidingWindowStore : ISlidingWindowStore
{
    // The member is the time and the number of members before it: two requests in the same millisecond see different counts,
    // because the count only grows within a millisecond, so no member is ever overwritten. A refused request is not recorded.
    // PEXPIRE removes a key that nobody has asked about for a window, so idle people leave Redis by themselves. When the limit
    // is reached the oldest member says when a place is free again. A script is used in place of MULTI, because the decision
    // depends on what the commands read.
    private static readonly LuaScript Script = LuaScript.Prepare("""
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local window = tonumber(@window)
        redis.call('ZREMRANGEBYSCORE', @key, '-inf', now - window)
        local count = redis.call('ZCARD', @key)
        if count < tonumber(@limit) then
            redis.call('ZADD', @key, now, now .. '-' .. count)
            redis.call('PEXPIRE', @key, window)
            return {1, 0}
        end
        local oldest = redis.call('ZRANGE', @key, 0, 0, 'WITHSCORES')
        return {0, tonumber(oldest[2]) + window - now}
        """);

    private readonly RedisConnection _redis;

    public RedisSlidingWindowStore(RedisConnection redis)
    {
        _redis = redis;
    }

    public async Task<RateLimitDecision> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        var database = await _redis.GetDatabaseAsync().WaitAsync(cancellationToken);
        var result = (RedisResult[])(await Script.EvaluateAsync(
            database, new { key = (RedisKey)key, limit, window = (long)window.TotalMilliseconds }))!;

        return (int)result[0] == 1
            ? new RateLimitDecision(true)
            : new RateLimitDecision(false, TimeSpan.FromMilliseconds((long)result[1]));
    }
}
