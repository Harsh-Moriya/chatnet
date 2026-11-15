using System.Globalization;
using ChatNET.API.Calls.Models;
using ChatNET.API.Presence;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatNET.API.Calls;

public enum CallState
{
    Ringing,
    Active,

    // It has ended and its end is being recorded; the Redis keys are removed when that is done.
    Ending,
}

// A call as Redis holds it. A connection is empty while that side is away (its hub connection dropped and it has not come back).
public sealed record CallSnapshot(
    Guid Id,
    Guid ConversationId,
    Guid CallerId,
    Guid CalleeId,
    CallMedia Media,
    CallState State,
    DateTime StartedAt,
    DateTime? AnsweredAt,
    string CallerConnection,
    string CalleeConnection,
    DateTime? EndedAt,
    CallEndReason? Reason)
{
    // The call as it is written to the call log when it has ended.
    public Call ToCall() => new()
    {
        Id = Id,
        ConversationId = ConversationId,
        CallerId = CallerId,
        CalleeId = CalleeId,
        Media = Media,
        StartedAt = StartedAt,
        AnsweredAt = AnsweredAt,
        EndedAt = EndedAt ?? throw new InvalidOperationException("A call that has not ended has no end to record."),
        EndReason = Reason ?? throw new InvalidOperationException("A call that has not ended has no reason to record."),
    };
}

public enum StartOutcome { Started, CallerBusy, CalleeBusy }

public enum AcceptOutcome { Accepted, NotFound, NotRinging, NotTheCallee }

public enum EndOutcome
{
    // This caller ended it, and records it and tells the people.
    Ended,
    NotFound,

    // Somebody else is finishing it.
    AlreadyEnding,
    NotInTheCall,

    // Asked by the system, but the call is not due any more.
    NotDue,
}

public enum RejoinOutcome { Rejoined, NotFound, WrongState, NotInTheCall, BadToken }

public enum SignalOutcome { Routed, NotActive, NotASide, LimitReached, PeerAway }

public sealed record StartResult(StartOutcome Outcome, DateTime At, DateTime RingsUntil);
public sealed record AcceptResult(AcceptOutcome Outcome, CallSnapshot? Call);
public sealed record EndResult(EndOutcome Outcome, CallSnapshot? Call);
public sealed record RejoinResult(RejoinOutcome Outcome, CallSnapshot? Call);
public sealed record SignalRoute(SignalOutcome Outcome, string TargetConnection);
public sealed record DueCall(Guid Id, CallState State);

// What a new call needs of the caller: made by the service, which has checked who may call whom.
public sealed record NewCall(Guid Id, Guid ConversationId, Guid CallerId, Guid CalleeId, CallMedia Media);

// The live state of calls, in Redis, changed only by the scripts in CallScripts. It holds no rules about who may call whom (the service
// checks that); it holds the rules that have to be settled between instances: one call at a time per person, one accept, one end, a lease
// for each side, and who a signal may go to.
public sealed class CallStore
{
    private const string KeyPrefix = "chatnet:call:";

    // How long the keys of a call live if nobody finishes it (every instance dead, nobody sweeping): a safety net, not the way a call
    // ends.
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(6);

    private static readonly RedisKey DeadlinesKey = "chatnet:calls:deadlines";

    private static readonly LuaScript StartScript = LuaScript.Prepare(CallScripts.Start);
    private static readonly LuaScript AcceptScript = LuaScript.Prepare(CallScripts.Accept);
    private static readonly LuaScript EndScript = LuaScript.Prepare(CallScripts.End);
    private static readonly LuaScript FinishScript = LuaScript.Prepare(CallScripts.Finish);
    private static readonly LuaScript RenewScript = LuaScript.Prepare(CallScripts.Renew);
    private static readonly LuaScript DetachScript = LuaScript.Prepare(CallScripts.Detach);
    private static readonly LuaScript RejoinScript = LuaScript.Prepare(CallScripts.Rejoin);
    private static readonly LuaScript SignalScript = LuaScript.Prepare(CallScripts.Signal);
    private static readonly LuaScript DueScript = LuaScript.Prepare(CallScripts.Due);

    private static readonly RedisValue[] SnapshotFields =
        ["conv", "caller", "callee", "media", "state", "startedAt", "answeredAt", "callerConn", "calleeConn", "endedAt", "reason"];

    private readonly RedisConnection _redis;
    private readonly CallOptions _options;

    public CallStore(RedisConnection redis, IOptions<CallOptions> options)
    {
        _redis = redis;
        _options = options.Value;
    }

    // ##### Transitions #####

    public async Task<StartResult> StartAsync(NewCall call, string connectionId, string tokenHash, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(StartScript, new
        {
            callKey = CallKey(call.Id),
            deadlinesKey = DeadlinesKey,
            callerMarker = UserKey(call.CallerId),
            calleeMarker = UserKey(call.CalleeId),
            callPrefix = KeyPrefix,
            callId = call.Id.ToString(),
            conv = call.ConversationId.ToString(),
            caller = call.CallerId.ToString(),
            callee = call.CalleeId.ToString(),
            media = call.Media.ToString(),
            connectionId,
            tokenHash,
            ringMs = (long)_options.RingTimeout.TotalMilliseconds,
            leaseMs = (long)_options.Lease.TotalMilliseconds,
            keyTtlMs = (long)KeyTtl.TotalMilliseconds,
        }, cancellationToken);

        var outcome = Code(reply) switch
        {
            1 => StartOutcome.CallerBusy,
            2 => StartOutcome.CalleeBusy,
            _ => StartOutcome.Started,
        };

        return new StartResult(outcome, At(reply), outcome == StartOutcome.Started ? FromMilliseconds(Long(reply, 2)) : default);
    }

    public async Task<AcceptResult> AcceptAsync(
        Guid callId, Guid userId, string connectionId, string tokenHash, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(AcceptScript, new
        {
            callKey = CallKey(callId),
            deadlinesKey = DeadlinesKey,
            callId = callId.ToString(),
            userId = userId.ToString(),
            connectionId,
            tokenHash,
            leaseMs = (long)_options.Lease.TotalMilliseconds,
        }, cancellationToken);

        return Code(reply) switch
        {
            0 => new AcceptResult(AcceptOutcome.Accepted, ReadSnapshot(callId, reply, 2)),
            1 => new AcceptResult(AcceptOutcome.NotFound, null),
            2 => new AcceptResult(AcceptOutcome.NotRinging, null),
            _ => new AcceptResult(AcceptOutcome.NotTheCallee, null),
        };
    }

    // A person ends it (userId), or the system does because it is due (userId null). failed says the person reported that the media failed.
    public async Task<EndResult> EndAsync(Guid callId, Guid? userId, bool failed, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(EndScript, new
        {
            callKey = CallKey(callId),
            deadlinesKey = DeadlinesKey,
            callId = callId.ToString(),
            userId = userId?.ToString() ?? string.Empty,
            failed = failed ? "1" : "0",
            retryMs = (long)_options.EndingRetry.TotalMilliseconds,
        }, cancellationToken);

        return Code(reply) switch
        {
            0 => new EndResult(EndOutcome.Ended, ReadSnapshot(callId, reply, 2)),
            1 => new EndResult(EndOutcome.NotFound, null),
            2 => new EndResult(EndOutcome.AlreadyEnding, null),
            3 => new EndResult(EndOutcome.NotInTheCall, null),
            _ => new EndResult(EndOutcome.NotDue, null),
        };
    }

    // The last step, after the call is recorded. The busy markers are removed only if they still name this call.
    public async Task FinishAsync(CallSnapshot call, CancellationToken cancellationToken)
    {
        await RunAsync(FinishScript, new
        {
            callKey = CallKey(call.Id),
            deadlinesKey = DeadlinesKey,
            callerMarker = UserKey(call.CallerId),
            calleeMarker = UserKey(call.CalleeId),
            callId = call.Id.ToString(),
        }, cancellationToken);
    }

    // True when the connection is still that side's current one and its lease was extended.
    public async Task<bool> RenewAsync(Guid callId, Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(RenewScript, new
        {
            callKey = CallKey(callId),
            deadlinesKey = DeadlinesKey,
            callId = callId.ToString(),
            userId = userId.ToString(),
            connectionId,
            leaseMs = (long)_options.Lease.TotalMilliseconds,
        }, cancellationToken);

        return Code(reply) == 0;
    }

    // A connection of a side closed: if it was that side's current one, the side is away and has the grace to come back.
    public async Task<bool> DetachAsync(Guid callId, Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(DetachScript, new
        {
            callKey = CallKey(callId),
            deadlinesKey = DeadlinesKey,
            callId = callId.ToString(),
            userId = userId.ToString(),
            connectionId,
            graceMs = (long)_options.ReconnectGrace.TotalMilliseconds,
        }, cancellationToken);

        return Code(reply) == 0;
    }

    public async Task<RejoinResult> RejoinAsync(
        Guid callId, Guid userId, string connectionId, string tokenHash, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(RejoinScript, new
        {
            callKey = CallKey(callId),
            deadlinesKey = DeadlinesKey,
            callId = callId.ToString(),
            userId = userId.ToString(),
            connectionId,
            tokenHash,
            leaseMs = (long)_options.Lease.TotalMilliseconds,
        }, cancellationToken);

        return Code(reply) switch
        {
            0 => new RejoinResult(RejoinOutcome.Rejoined, ReadSnapshot(callId, reply, 2)),
            1 => new RejoinResult(RejoinOutcome.NotFound, null),
            2 => new RejoinResult(RejoinOutcome.WrongState, null),
            3 => new RejoinResult(RejoinOutcome.NotInTheCall, null),
            _ => new RejoinResult(RejoinOutcome.BadToken, null),
        };
    }

    public async Task<SignalRoute> RouteSignalAsync(Guid callId, Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(SignalScript, new
        {
            callKey = CallKey(callId),
            userId = userId.ToString(),
            connectionId,
            maxSignals = _options.MaxSignalsPerCall,
        }, cancellationToken);

        return Code(reply) switch
        {
            0 => new SignalRoute(SignalOutcome.Routed, (string)reply[2]!),
            2 => new SignalRoute(SignalOutcome.NotActive, string.Empty),
            3 => new SignalRoute(SignalOutcome.NotASide, string.Empty),
            5 => new SignalRoute(SignalOutcome.LimitReached, string.Empty),
            _ => new SignalRoute(SignalOutcome.PeerAway, string.Empty),
        };
    }

    // ##### Reading #####

    // The calls that are due now, at most `max`. A call that is ending comes back once per retry interval.
    public async Task<IReadOnlyList<DueCall>> DueAsync(int max, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(DueScript, new
        {
            deadlinesKey = DeadlinesKey,
            callPrefix = KeyPrefix,
            max,
            retryMs = (long)_options.EndingRetry.TotalMilliseconds,
        }, cancellationToken);

        var due = new List<DueCall>();
        for (var i = 1; i + 1 < reply.Length; i += 2)
            due.Add(new DueCall(Guid.Parse((string)reply[i]!), ParseState((string)reply[i + 1]!)));

        return due;
    }

    public async Task<CallSnapshot?> GetAsync(Guid callId, CancellationToken cancellationToken)
    {
        var db = await _redis.GetDatabaseAsync();
        var values = await db.HashGetAsync(CallKey(callId), SnapshotFields).WaitAsync(cancellationToken);

        return values[0].IsNull ? null : ToSnapshot(callId, values.Select(v => v.IsNull ? string.Empty : (string)v!).ToArray(), 0);
    }

    // The call the person is in, or null. Reads the busy marker and then the call, so a call that ends in between is null too.
    public async Task<CallSnapshot?> GetOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        var db = await _redis.GetDatabaseAsync();
        var marker = await db.StringGetAsync(UserKey(userId)).WaitAsync(cancellationToken);

        return marker.IsNull ? null : await GetAsync(Guid.Parse((string)marker!), cancellationToken);
    }

    // ##### Plumbing #####

    private async Task<RedisResult[]> RunAsync(LuaScript script, object parameters, CancellationToken cancellationToken)
    {
        var db = await _redis.GetDatabaseAsync();
        return (RedisResult[])(await script.EvaluateAsync(db, parameters).WaitAsync(cancellationToken))!;
    }

    private static int Code(RedisResult[] reply) => (int)(long)reply[0];

    private static long Long(RedisResult[] reply, int index) => (long)reply[index];

    // { code, time, ... } where the time is Redis's, the moment the script ran.
    private static DateTime At(RedisResult[] reply) => FromMilliseconds(Long(reply, 1));

    private static CallSnapshot ReadSnapshot(Guid callId, RedisResult[] reply, int offset) =>
        ToSnapshot(callId, reply.Skip(offset).Select(r => (string?)r ?? string.Empty).ToArray(), 0);

    private static CallSnapshot ToSnapshot(Guid callId, string[] fields, int offset)
    {
        string F(int i) => fields[offset + i];
        DateTime? Time(int i) => F(i).Length == 0 ? null : FromMilliseconds(long.Parse(F(i), CultureInfo.InvariantCulture));

        return new CallSnapshot(
            callId,
            Guid.Parse(F(0)),
            Guid.Parse(F(1)),
            Guid.Parse(F(2)),
            Enum.Parse<CallMedia>(F(3)),
            ParseState(F(4)),
            Time(5)!.Value,
            Time(6),
            F(7),
            F(8),
            Time(9),
            F(10).Length == 0 ? null : Enum.Parse<CallEndReason>(F(10)));
    }

    private static CallState ParseState(string state) => state switch
    {
        "ringing" => CallState.Ringing,
        "active" => CallState.Active,
        _ => CallState.Ending,
    };

    private static RedisKey CallKey(Guid callId) => $"{KeyPrefix}{callId}";

    private static RedisKey UserKey(Guid userId) => $"{KeyPrefix}user:{userId}";

    private static DateTime FromMilliseconds(long unixMilliseconds) => DateTime.UnixEpoch.AddMilliseconds(unixMilliseconds);
}
