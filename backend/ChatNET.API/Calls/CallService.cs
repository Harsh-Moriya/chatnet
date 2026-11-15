using System.Security.Cryptography;
using System.Text;
using ChatNET.API.Calls.Ice;
using ChatNET.API.Calls.Models;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Outbox.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Push;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatNET.API.Calls;

// The rules of a call, over the live state in Redis (CallStore) and the call log in the database (CallLog): who may call whom, what each
// connection may do, and what everybody is told. One instance for the whole application, like PresenceService, because the connections
// this instance holds are the ones it renews.
//
// A refused hub method throws a HubException whose message is one of CallErrors, so a client can tell what to do. Redis being unavailable
// refuses a method with calls-unavailable, and costs the background steps one round (they log and carry on). Nothing here logs what a signal
// says: a session description and a candidate contain network addresses. The logs carry ids.
public sealed class CallService
{
    // Long enough for a busy Redis, short enough that a dead one does not hold up a connection.
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);

    // Calls looked at per sweep round, so one round is small however many are due at once.
    private const int SweepBatchSize = 100;

    private readonly CallStore _store;
    private readonly IceServers _ice;
    private readonly CallStartLimiter _limiter;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly IServiceScopeFactory _scopes;
    private readonly ConnectionRegistry _registry;
    private readonly CallOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CallService> _logger;

    public CallService(
        CallStore store,
        IceServers ice,
        CallStartLimiter limiter,
        IHubContext<ChatHub, IChatClient> hub,
        IServiceScopeFactory scopes,
        ConnectionRegistry registry,
        IOptions<CallOptions> options,
        TimeProvider time,
        ILogger<CallService> logger)
    {
        _store = store;
        _ice = ice;
        _limiter = limiter;
        _hub = hub;
        _scopes = scopes;
        _registry = registry;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    // ##### Called by the hub #####

    public async Task<CallStartedDto> StartAsync(
        Guid callerId, string connectionId, Guid conversationId, CallMedia media, CancellationToken cancellationToken)
    {
        if (!await _limiter.MayStartAsync(callerId, cancellationToken))
        {
            _logger.LogWarning("User {UserId} started calls faster than allowed", callerId);
            throw new HubException(CallErrors.RateLimited);
        }

        using var scope = _scopes.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();

        if (!await conversations.IsActiveMemberAsync(conversationId, callerId, cancellationToken))
        {
            _logger.LogWarning("User {UserId} tried to call in conversation {ConversationId} without being an active member", callerId, conversationId);
            throw new HubException(CallErrors.NotAMember);
        }

        var peer = await conversations.GetDirectPeerAsync(conversationId, callerId, cancellationToken)
            ?? throw new HubException(CallErrors.NotADirectConversation);

        if (peer.PeerIsDeactivated || peer.SelfIsDeactivated)
            throw new HubException(CallErrors.PeerUnavailable);

        var call = new NewCall(Guid.CreateVersion7(), conversationId, callerId, peer.PeerId, media);
        var token = NewToken();
        var started = await RedisAsync(step => _store.StartAsync(call, connectionId, HashOf(token), step), cancellationToken);

        switch (started.Outcome)
        {
            case StartOutcome.CallerBusy:
                throw new HubException(CallErrors.AlreadyInACall);

            case StartOutcome.CalleeBusy:
                await RecordBusyAsync(scope, call, cancellationToken);
                throw new HubException(CallErrors.CalleeBusy);
        }

        _logger.LogInformation(
            "Call {CallId} started by {UserId} to {CalleeId} in conversation {ConversationId} ({Media})",
            call.Id, callerId, peer.PeerId, conversationId, media);

        var callerName = await DisplayNameAsync(scope, callerId, cancellationToken);

        await TellAsync(async () =>
            await _hub.Clients.User(peer.PeerId.ToString()).IncomingCall(new IncomingCallDto(
                call.Id, conversationId, callerId, callerName, media, started.RingsUntil)),
            call.Id);

        await AlertAwayAsync(scope, new CallAlertRequested(call.Id, conversationId, callerId, peer.PeerId, callerName, media), cancellationToken);

        return new CallStartedDto(call.Id, token, started.RingsUntil, _ice.For(callerId));
    }

    public async Task<CallAcceptedDto> AcceptAsync(Guid userId, string connectionId, Guid callId, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var result = await RedisAsync(step => _store.AcceptAsync(callId, userId, connectionId, HashOf(token), step), cancellationToken);

        switch (result.Outcome)
        {
            case AcceptOutcome.NotFound:
                throw new HubException(CallErrors.CallNotFound);

            case AcceptOutcome.NotRinging:
                throw new HubException(CallErrors.CallNotRinging);

            case AcceptOutcome.NotTheCallee:
                _logger.LogWarning("User {UserId} tried to answer call {CallId}, which is not theirs to answer", userId, callId);
                throw new HubException(CallErrors.NotTheCallee);
        }

        var call = result.Call!;
        _logger.LogInformation("Call {CallId} answered by {UserId}", callId, userId);

        // To both people: the caller's tab makes its offer, and a tab of the callee that is still ringing stops.
        await TellAsync(async () =>
        {
            await _hub.Clients.User(call.CallerId.ToString()).CallAnswered(callId);
            await _hub.Clients.User(call.CalleeId.ToString()).CallAnswered(callId);
        }, callId);

        return new CallAcceptedDto(token, call.Media, call.CallerId, _ice.For(userId));
    }

    // A call that is not there, or that somebody else is already ending, is not an error: a hang-up that arrives twice is the same hang-up.
    public async Task EndAsync(Guid userId, Guid callId, bool failed, CancellationToken cancellationToken)
    {
        var result = await RedisAsync(step => _store.EndAsync(callId, userId, failed, step), cancellationToken);

        switch (result.Outcome)
        {
            case EndOutcome.NotInTheCall:
                _logger.LogWarning("User {UserId} tried to end call {CallId}, which they are not in", userId, callId);
                throw new HubException(CallErrors.CallNotFound);

            case EndOutcome.Ended:
                await FinishAsync(result.Call!, cancellationToken);
                break;
        }
    }

    // True when the signal was handed on; false when the other side is away (it is not kept: the side that comes back starts again).
    public async Task<bool> RouteSignalAsync(
        Guid userId, string connectionId, Guid callId, CallSignalType type, string payload, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(payload) > _options.MaxSignalBytes)
            throw new HubException(CallErrors.SignalTooLarge);

        var route = await RedisAsync(step => _store.RouteSignalAsync(callId, userId, connectionId, step), cancellationToken);

        switch (route.Outcome)
        {
            case SignalOutcome.NotASide:
                _logger.LogWarning("Connection {ConnectionId} of user {UserId} sent a signal on call {CallId}, which it is not a side of", connectionId, userId, callId);
                throw new HubException(CallErrors.NotInTheCall);

            case SignalOutcome.LimitReached:
                throw new HubException(CallErrors.SignalLimit);

            case SignalOutcome.Routed:
                await _hub.Clients.Client(route.TargetConnection).CallSignal(callId, type, payload);
                return true;

            default:
                return false;
        }
    }

    public async Task<CallRejoinedDto> RejoinAsync(
        Guid userId, string connectionId, Guid callId, string rejoinToken, CancellationToken cancellationToken)
    {
        var result = await RedisAsync(step => _store.RejoinAsync(callId, userId, connectionId, HashOf(rejoinToken), step), cancellationToken);

        switch (result.Outcome)
        {
            case RejoinOutcome.NotFound:
                throw new HubException(CallErrors.CallNotFound);

            case RejoinOutcome.WrongState:
                throw new HubException(CallErrors.CallEnded);

            case RejoinOutcome.NotInTheCall:
                throw new HubException(CallErrors.NotInTheCall);

            case RejoinOutcome.BadToken:
                _logger.LogWarning("User {UserId} tried to rejoin call {CallId} with a token that is not theirs", userId, callId);
                throw new HubException(CallErrors.BadToken);
        }

        _logger.LogInformation("User {UserId} rejoined call {CallId} on a new connection", userId, callId);
        return new CallRejoinedDto(result.Call!.State, result.Call.Media, _ice.For(userId));
    }

    // A connection that was a side of a call closed: that side has the grace to come back. Never throws, since it runs as a connection ends.
    public async Task DetachAsync(Guid userId, string connectionId, Guid callId)
    {
        try
        {
            using var step = Step(default);
            await _store.DetachAsync(callId, userId, connectionId, step.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark connection {ConnectionId} of user {UserId} away from call {CallId}; its lease will run out", connectionId, userId, callId);
        }
    }

    // The call that is ringing for the person, if there is one: a tab that opens while the phone is ringing rings too.
    public async Task<IncomingCallDto?> IncomingForAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            using var step = Step(cancellationToken);
            var call = await _store.GetOfAsync(userId, step.Token);
            if (call is not { State: CallState.Ringing } || call.CalleeId != userId)
                return null;

            using var scope = _scopes.CreateScope();
            return new IncomingCallDto(
                call.Id, call.ConversationId, call.CallerId, await DisplayNameAsync(scope, call.CallerId, cancellationToken), call.Media,
                call.StartedAt + _options.RingTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not look for a call ringing for user {UserId}", userId);
            return null;
        }
    }

    // ##### Called by the background service #####

    // Extends the lease of every connection this instance holds that is a side of a call. A connection whose call is gone, or that was
    // replaced by a rejoin, is told so by the store and forgets it.
    public async Task RenewHeldAsync(CancellationToken cancellationToken)
    {
        foreach (var (userId, context) in _registry.All())
        {
            if (ConnectionState.CallOf(context.Items) is not { } callId)
                continue;

            try
            {
                using var step = Step(cancellationToken);
                if (!await _store.RenewAsync(callId, userId, context.ConnectionId, step.Token))
                    ConnectionState.SetCall(context.Items, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not renew call {CallId} for connection {ConnectionId}", callId, context.ConnectionId);
            }
        }
    }

    // Ends every call that is due (a ring that ran out, a side whose lease or grace ended) and finishes every call whose end was started and
    // not completed (the instance that started it stopped). Safe to run on every instance at once: the store lets exactly one end a call.
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<DueCall> due;
            using (var step = Step(cancellationToken))
                due = await _store.DueAsync(SweepBatchSize, step.Token);

            foreach (var call in due)
            {
                try
                {
                    await SweepOneAsync(call, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not end call {CallId}; the next round will try again", call.Id);
                }
            }

            if (due.Count < SweepBatchSize)
                return;
        }
    }

    private async Task SweepOneAsync(DueCall due, CancellationToken cancellationToken)
    {
        CallSnapshot? ended;
        using (var step = Step(cancellationToken))
        {
            ended = due.State == CallState.Ending
                ? await _store.GetAsync(due.Id, step.Token)
                : (await _store.EndAsync(due.Id, null, failed: false, step.Token)) is { Outcome: EndOutcome.Ended } result ? result.Call : null;
        }

        if (ended is { State: CallState.Ending })
            await FinishAsync(ended, cancellationToken);
    }

    // ##### Ending #####

    // What follows the call being ended, the same whoever got there first and however often it is retried: the call is recorded (a repeat is
    // a no-op), its keys go, and everybody is told. If the recording fails the keys stay, the call stays "ending", and the sweep comes
    // back for it; the people are told in any case, since the call is over for them.
    private async Task FinishAsync(CallSnapshot ended, CancellationToken cancellationToken)
    {
        var recorded = false;
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CallLog>().RecordAsync(ended.ToCall(), cancellationToken);
            recorded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not record call {CallId}; it will be tried again", ended.Id);
        }

        if (recorded)
        {
            try
            {
                using var step = Step(cancellationToken);
                await _store.FinishAsync(ended, step.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not remove the live state of call {CallId}; the next round will", ended.Id);
            }
        }

        _logger.LogInformation("Call {CallId} ended as {Reason}", ended.Id, ended.Reason);

        var told = new CallEndedDto(ended.Id, ended.Reason!.Value, ended.EndedAt!.Value);
        await TellAsync(async () =>
        {
            await _hub.Clients.User(ended.CallerId.ToString()).CallEnded(told);
            await _hub.Clients.User(ended.CalleeId.ToString()).CallEnded(told);
        }, ended.Id);
    }

    // The callee's browsers that are away are alerted by push: the row that asks for it is saved here, and the push is made at once but not
    // waited for, so the caller is not held up by a push service. If the push fails the relay takes the row.
    private async Task AlertAwayAsync(IServiceScope scope, CallAlertRequested alert, CancellationToken cancellationToken)
    {
        try
        {
            if (scope.ServiceProvider.GetRequiredService<PushAlertRows>().ForCall(alert) is not { } row)
                return;

            await scope.ServiceProvider.GetRequiredService<IOutboxRepository>().AddAsync(row, cancellationToken);
            _ = DeliverAlertAsync(row, alert);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not save the alert for call {CallId}; the callee is not alerted by push", alert.CallId);
        }
    }

    // Not awaited by anyone, so it handles its own failures (the publisher logs a push that failed and leaves the row for the relay).
    private async Task DeliverAlertAsync(OutboxMessage row, CallAlertRequested alert)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var alerts = scope.ServiceProvider.GetRequiredService<PushAlerts>();

            await scope.ServiceProvider.GetRequiredService<OutboxPublisher>()
                .DeliverAsync(row, () => alerts.AlertForCallAsync(alert, CancellationToken.None), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push the alert for call {CallId}; the relay will try", alert.CallId);
        }
    }

    // A caller who calls somebody who is already in a call has tried and failed: the callee sees it as a missed call.
    private async Task RecordBusyAsync(IServiceScope scope, NewCall call, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        try
        {
            await scope.ServiceProvider.GetRequiredService<CallLog>().RecordAsync(new Call
            {
                Id = call.Id,
                ConversationId = call.ConversationId,
                CallerId = call.CallerId,
                CalleeId = call.CalleeId,
                Media = call.Media,
                StartedAt = now,
                AnsweredAt = null,
                EndedAt = now,
                EndReason = CallEndReason.Busy,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not record the call {CallId} that met a busy line", call.Id);
        }
    }

    // ##### Plumbing #####

    // A push to people's connections that fails (the backplane is down) must not undo what happened: the call is in Redis and the people
    // find out when they reconnect or when it ends.
    private async Task TellAsync(Func<Task> tell, Guid callId)
    {
        try
        {
            await tell();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell the people of call {CallId}", callId);
        }
    }

    private static async Task<string> DisplayNameAsync(IServiceScope scope, Guid userId, CancellationToken cancellationToken)
        => await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Someone";

    // A step against Redis is bounded in time; for a hub method, running out of time or an error from Redis is "calls are unavailable".
    private async Task<T> RedisAsync<T>(Func<CancellationToken, Task<T>> step, CancellationToken cancellationToken)
    {
        using var bounded = Step(cancellationToken);

        try
        {
            return await step(bounded.Token);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "The live state of calls could not be reached");
            throw new HubException(CallErrors.Unavailable);
        }
    }

    private static CancellationTokenSource Step(CancellationToken cancellationToken)
    {
        var step = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        step.CancelAfter(StepTimeout);
        return step;
    }

    private static string NewToken() => Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
