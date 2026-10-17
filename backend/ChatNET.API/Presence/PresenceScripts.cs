namespace ChatNET.API.Presence;

// The Lua scripts Redis runs for presence. Redis runs a script as one uninterruptible step, so
// each of these is atomic across every server that talks to the same Redis: two servers cannot
// interleave their reads and writes the way two separate commands could.
//
// Two sorted sets hold the state (a sorted set is a set of values, each with a numeric score,
// ordered by score; here the score is a time in milliseconds since 1970):
//
//   user key      member = a connection id,  score = when that connection's entry expires
//   expiring key  member = a user id,        score = when the user's newest entry expires
//
// The user key counts a person's live connections (they may have several tabs). The expiring key is
// the set of people whose contacts have been told they are online: a user is added the moment they
// come online and removed the moment they are announced offline, and the score lets a sweeper find
// those whose entries all expired (a server that died) without scanning every user. Because it
// follows what was announced and not what is alive this instant, a user in the short wait before
// "offline" is still online as far as anyone is concerned, and a snapshot taken then agrees with
// the events everybody else has seen.
//
// A third key per user, the closing key, is a plain value that exists only while that wait runs: it
// is written with an expiry of the grace period when the user's last connection closes, so Redis
// itself keeps the clock. Whoever comes to finish the user finds it there and leaves them alone, and
// because every close rewrites it, the wait is always counted from the most recent close.
//
// Each script reads the time from Redis (TIME) so that servers with slightly different clocks agree
// on what has expired. The @names are filled in by StackExchange.Redis.
internal static class PresenceScripts
{
    private const string Now = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        """;

    // A connection connected, or its refresh came due. The connection's entry is written, and the user
    // is put in the expiring set. ZADD returns 1 only when the user was not in the set yet, which is
    // exactly "tell their contacts they came online". Returns { that 1 or 0 ; Redis time }. Expired
    // entries are not dropped here; Settle does that before it decides anything.
    public const string Touch = Now + """

        local expiry = now + tonumber(@ttlMs)
        redis.call('ZADD', @userKey, expiry, @connectionId)
        redis.call('PEXPIRE', @userKey, @keyTtlMs)
        local added = redis.call('ZADD', @expiringKey, expiry, @userId)
        return { added, now }
        """;

    // A connection went away (when connectionId is given, with the grace to wait), or the sweeper or a
    // grace timer is checking on a user (no connection, no grace). Returns
    // { outcome ; Redis time ; milliseconds left of the wait } where the outcome is
    //   0 the user still has a live connection (their expiring score is set to the newest entry),
    //   1 they have none, no wait is running, and this call removed them: the one to announce it,
    //   2 they have none and a wait is running: left in place, the third value is what is left of it,
    //   3 they have none, no wait is running, but someone else already removed them.
    // Removing the user from the expiring set is the single decision that makes exactly one caller
    // the announcer, however many race.
    public const string Settle = Now + """

        if @connectionId ~= '' then redis.call('ZREM', @userKey, @connectionId) end
        redis.call('ZREMRANGEBYSCORE', @userKey, '-inf', now)
        local newest = redis.call('ZREVRANGE', @userKey, 0, 0, 'WITHSCORES')
        if #newest > 0 then
            redis.call('ZADD', @expiringKey, newest[2], @userId)
            return { 0, now, 0 }
        end
        if tonumber(@graceMs) > 0 then
            redis.call('SET', @closingKey, '1', 'PX', @graceMs)
        end
        local left = redis.call('PTTL', @closingKey)
        if left > 0 then return { 2, now, left } end
        if redis.call('ZREM', @expiringKey, @userId) == 1 then return { 1, now, 0 } end
        return { 3, now, 0 }
        """;

    // Users in the expiring set whose newest entry has expired: { Redis time, id, score, id, score ... }.
    public const string Expired = Now + """

        local rows = redis.call('ZRANGEBYSCORE', @expiringKey, '-inf', now, 'WITHSCORES', 'LIMIT', 0, @max)
        local out = { now }
        for i = 1, #rows do out[#out + 1] = rows[i] end
        return out
        """;
}
