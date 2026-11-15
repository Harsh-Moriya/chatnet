namespace ChatNET.API.Calls;

// The Lua scripts Redis runs for the live state of calls. Redis runs a script as one uninterruptible step, so each transition is atomic
// across every API instance that talks to the same Redis: an accept and a decline that arrive together, two hang-ups, or an accept and the
// ring running out are settled by whichever script runs first, and the other finds the state the first left. The state, in three kinds of
// key (the prefix is passed in, so a script can look a call up by the id a busy marker holds):
//
//   call key      a hash: the two people, the media, the state (ringing, active, ending), the times, the connection of each side
//                 (empty while that side is away), the SHA-256 of each side's rejoin token, the lease of each side, a signal counter
//   user key      the id of the call a person is in: the busy marker (a person is in one call at a time, on any device)
//   deadlines     a sorted set: member = a call id, score = when it is next due. Ringing: when the ring ends (earlier if the caller's
//                 connection dropped: its grace). Active: the earlier of the two sides' leases. Ending: when to try to finish it again.
//
// Each script reads the time from Redis (TIME), so instances with slightly different clocks agree. The @names are filled in by
// StackExchange.Redis; the keys that are not known in advance are built here from the prefix, which a single Redis allows.
//
// Every script answers { code, Redis time, ... }; 0 is success. The codes that are not shared are named at each script.
internal static class CallScripts
{
    // The fields of a call that are read back, in this order (see CallStore.ReadSnapshot). A missing field is an empty string.
    public const string SnapshotFields =
        "'conv', 'caller', 'callee', 'media', 'state', 'startedAt', 'answeredAt', 'callerConn', 'calleeConn', 'endedAt', 'reason'";

    private const string Prelude = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

        local function snapshot(key)
            local v = redis.call('HMGET', key, 'conv', 'caller', 'callee', 'media', 'state', 'startedAt', 'answeredAt', 'callerConn', 'calleeConn', 'endedAt', 'reason')
            for i = 1, #v do if v[i] == false then v[i] = '' end end
            return v
        end

        -- 'caller', 'callee', or nil for somebody who is neither.
        local function roleOf(key, userId)
            if redis.call('HGET', key, 'caller') == userId then return 'caller' end
            if redis.call('HGET', key, 'callee') == userId then return 'callee' end
            return nil
        end

        -- When the call is next due, from what it is now.
        local function reschedule(key, deadlines, id)
            local h = redis.call('HMGET', key, 'state', 'ringUntil', 'callerConn', 'callerUntil', 'calleeUntil')
            if h[1] == 'ringing' then
                local due = tonumber(h[2])
                if h[3] == '' then due = math.min(due, tonumber(h[4])) end
                redis.call('ZADD', deadlines, due, id)
            elseif h[1] == 'active' then
                redis.call('ZADD', deadlines, math.min(tonumber(h[4]), tonumber(h[5])), id)
            end
        end
        """;

    // A call starts. Answers { 1 ; time } when the caller is already in a call, { 2 ; time } when the callee is, and otherwise
    // { 0 ; time ; when the ring ends }. A busy marker whose call is gone (a crash between finishing steps, an expired key) is stale and
    // is taken as free.
    public const string Start = Prelude + """

        local function busy(marker)
            local id = redis.call('GET', marker)
            if not id then return false end
            if redis.call('EXISTS', @callPrefix .. id) == 1 then return true end
            redis.call('DEL', marker)
            return false
        end

        if busy(@callerMarker) then return { 1, now } end
        if busy(@calleeMarker) then return { 2, now } end

        local ringUntil = now + tonumber(@ringMs)
        redis.call('HSET', @callKey,
            'conv', @conv, 'caller', @caller, 'callee', @callee, 'media', @media,
            'state', 'ringing', 'startedAt', now, 'ringUntil', ringUntil, 'answeredAt', '', 'endedAt', '', 'reason', '',
            'callerConn', @connectionId, 'calleeConn', '', 'callerToken', @tokenHash, 'calleeToken', '',
            'callerUntil', now + tonumber(@leaseMs), 'calleeUntil', 0, 'signals', 0)
        redis.call('PEXPIRE', @callKey, @keyTtlMs)
        redis.call('SET', @callerMarker, @callId, 'PX', @keyTtlMs)
        redis.call('SET', @calleeMarker, @callId, 'PX', @keyTtlMs)
        redis.call('ZADD', @deadlinesKey, ringUntil, @callId)
        return { 0, now, ringUntil }
        """;

    // The callee answers. { 1 } there is no such call, { 2 } it is not ringing (answered, ended, being ended, or the ring has run out), { 3 } this person is
    // not its callee, otherwise { 0 ; time ; the call }. Only a ringing call can be answered, and answering is what makes it not
    // ringing, so two answers cannot both succeed.
    public const string Accept = Prelude + """

        if redis.call('EXISTS', @callKey) == 0 then return { 1, now } end
        local h = redis.call('HMGET', @callKey, 'state', 'callee', 'ringUntil')
        if h[1] ~= 'ringing' or now >= tonumber(h[3]) then return { 2, now } end
        if h[2] ~= @userId then return { 3, now } end

        redis.call('HSET', @callKey, 'state', 'active', 'answeredAt', now, 'calleeConn', @connectionId,
            'calleeToken', @tokenHash, 'calleeUntil', now + tonumber(@leaseMs))
        reschedule(@callKey, @deadlinesKey, @callId)
        return { 0, now, unpack(snapshot(@callKey)) }
        """;

    // A call ends, because a person asked (userId) or because it is due (userId empty). The reason is decided here, from the state this
    // finds, because that is what the race with an accept or a timeout is about:
    //   ringing: the caller ends it: Cancelled; the callee: Declined; due, and the caller's connection gone past its grace: Cancelled;
    //            due, otherwise: Missed
    //   active:  a person hangs up: Completed; a person says the media failed, or it is due (a lease or a grace ran out): Failed
    // { 1 } no such call, { 2 } it is already ending (somebody else finishes it), { 3 } this person is not in the call, { 5 } asked by
    // the system but the call is not due (it was renewed or rejoined since the caller looked), otherwise { 0 ; time ; the call as it was
    // ended }. Only the one that gets 0 records the call and tells the people.
    public const string End = Prelude + """

        if redis.call('EXISTS', @callKey) == 0 then return { 1, now } end
        local h = redis.call('HMGET', @callKey, 'state', 'ringUntil', 'callerConn', 'callerUntil')
        local state = h[1]
        if state == 'ending' then return { 2, now } end

        local role = nil
        if @userId ~= '' then
            role = roleOf(@callKey, @userId)
            if role == nil then return { 3, now } end
        else
            local due = redis.call('ZSCORE', @deadlinesKey, @callId)
            if not due or tonumber(due) > now then return { 5, now } end
        end

        local reason
        if state == 'ringing' then
            if role == 'caller' then reason = 'Cancelled'
            elseif role == 'callee' then reason = 'Declined'
            elseif h[3] == '' and tonumber(h[4]) <= now and tonumber(h[2]) > now then reason = 'Cancelled'
            else reason = 'Missed' end
        else
            if role == nil or @failed == '1' then reason = 'Failed' else reason = 'Completed' end
        end

        redis.call('HSET', @callKey, 'state', 'ending', 'endedAt', now, 'reason', reason)
        redis.call('ZADD', @deadlinesKey, now + tonumber(@retryMs), @callId)
        return { 0, now, unpack(snapshot(@callKey)) }
        """;

    // The last step of ending a call, once it is recorded: the keys go, and the busy markers with them only if they still name this call.
    // Safe to repeat.
    public const string Finish = Prelude + """

        if redis.call('GET', @callerMarker) == @callId then redis.call('DEL', @callerMarker) end
        if redis.call('GET', @calleeMarker) == @callId then redis.call('DEL', @calleeMarker) end
        redis.call('DEL', @callKey)
        redis.call('ZREM', @deadlinesKey, @callId)
        return { 0, now }
        """;

    // A side's connection renews its lease. { 1 } the call is gone or ending, { 3 } this is not that side's current connection (it was
    // replaced by a rejoin, or dropped), otherwise { 0 }. Only the current connection of a side may extend it.
    public const string Renew = Prelude + """

        local state = redis.call('HGET', @callKey, 'state')
        if state == false or state == 'ending' then return { 1, now } end
        local role = roleOf(@callKey, @userId)
        if role == nil or redis.call('HGET', @callKey, role .. 'Conn') ~= @connectionId then return { 3, now } end

        redis.call('HSET', @callKey, role .. 'Until', now + tonumber(@leaseMs))
        reschedule(@callKey, @deadlinesKey, @callId)
        return { 0, now }
        """;

    // A side's connection closed. If it is still that side's current connection the side is marked away and its lease becomes the grace
    // to come back. { 1 } gone or ending, { 3 } not the current connection (nothing to do), otherwise { 0 }.
    public const string Detach = Prelude + """

        local state = redis.call('HGET', @callKey, 'state')
        if state == false or state == 'ending' then return { 1, now } end
        local role = roleOf(@callKey, @userId)
        if role == nil or redis.call('HGET', @callKey, role .. 'Conn') ~= @connectionId then return { 3, now } end

        redis.call('HSET', @callKey, role .. 'Conn', '', role .. 'Until', now + tonumber(@graceMs))
        reschedule(@callKey, @deadlinesKey, @callId)
        return { 0, now }
        """;

    // The same person's new connection takes the side back with the token it was given. { 1 } gone, { 2 } the call is not ringing (for
    // the caller) or active, { 3 } not in the call, { 4 } the token is not the side's, otherwise { 0 ; time ; the call }.
    public const string Rejoin = Prelude + """

        if redis.call('EXISTS', @callKey) == 0 then return { 1, now } end
        local state = redis.call('HGET', @callKey, 'state')
        if state ~= 'ringing' and state ~= 'active' then return { 2, now } end
        local role = roleOf(@callKey, @userId)
        if role == nil then return { 3, now } end
        local token = redis.call('HGET', @callKey, role .. 'Token')
        if token == false or token == '' or token ~= @tokenHash then return { 4, now } end

        redis.call('HSET', @callKey, role .. 'Conn', @connectionId, role .. 'Until', now + tonumber(@leaseMs))
        reschedule(@callKey, @deadlinesKey, @callId)
        return { 0, now, unpack(snapshot(@callKey)) }
        """;

    // A signal from one side to the other. Only the current connection of a side may send, and only while the call is active.
    // { 2 } not active, { 3 } not a side's current connection, { 5 } the call has carried its share of signals, { 6 } the other side is
    // away (nothing is counted), otherwise { 0 ; time ; the connection to send it to }.
    public const string Signal = Prelude + """

        if redis.call('HGET', @callKey, 'state') ~= 'active' then return { 2, now } end
        local role = roleOf(@callKey, @userId)
        if role == nil or redis.call('HGET', @callKey, role .. 'Conn') ~= @connectionId then return { 3, now } end

        local other = 'callee'
        if role == 'callee' then other = 'caller' end
        local target = redis.call('HGET', @callKey, other .. 'Conn')
        if target == false or target == '' then return { 6, now } end

        if redis.call('HINCRBY', @callKey, 'signals', 1) > tonumber(@maxSignals) then return { 5, now } end
        return { 0, now, target }
        """;

    // The calls that are due: { time ; id, state, id, state ... }. A call that is ending is claimed for a retry by pushing its time out, so
    // of several instances looking at once (each script runs alone) only one gets it; a ringing or active one is ended by the script above, which settles who. An
    // entry whose call is gone is dropped.
    public const string Due = Prelude + """

        local ids = redis.call('ZRANGEBYSCORE', @deadlinesKey, '-inf', now, 'LIMIT', 0, @max)
        local out = { now }
        for _, id in ipairs(ids) do
            local state = redis.call('HGET', @callPrefix .. id, 'state')
            if state == false then
                redis.call('ZREM', @deadlinesKey, id)
            else
                if state == 'ending' then redis.call('ZADD', @deadlinesKey, now + tonumber(@retryMs), id) end
                out[#out + 1] = id
                out[#out + 1] = state
            end
        end
        return out
        """;
}
