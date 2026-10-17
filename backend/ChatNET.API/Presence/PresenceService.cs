using System.Collections.Concurrent;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Presence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Presence;

// Turns connections coming and going into presence changes: keeps the entries in Redis, and when
// someone's presence really changes tells the people who may see it.
//
// One instance for the whole application, because it remembers which connections THIS server holds:
// the refresh service renews exactly those, and nothing else renews a connection that is still open.
//
// Redis being unavailable must never stop anyone connecting or chatting, so every Redis step here is
// bounded in time and its failures are logged and dropped. The next refresh renews the entries of
// every connection this server still holds, which corrects whatever was missed.
public sealed class PresenceService
{
    // Long enough for a busy Redis, short enough that a dead one does not hold up a connection.
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);

    // Users asked about per sweep round, so one round is small however many entries expired at once.
    private const int SweepBatchSize = 100;

    private readonly IPresenceStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly PresenceOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PresenceService> _logger;

    // The connections this server holds, by connection id.
    private readonly ConcurrentDictionary<string, Guid> _connections = new();

    public PresenceService(
        IPresenceStore store,
        IServiceScopeFactory scopes,
        IHubContext<ChatHub, IChatClient> hub,
        IOptions<PresenceOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<PresenceService> logger)
    {
        _store = store;
        _scopes = scopes;
        _hub = hub;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    // ##### Connections #####

    public async Task ConnectedAsync(Guid userId, string connectionId)
    {
        // Remembered before Redis is asked, so a connection made while Redis is down is still renewed
        // (and announced) by the next refresh.
        _connections[connectionId] = userId;

        try
        {
            using var step = StepToken();
            var touched = await _store.TouchAsync(userId, connectionId, step.Token);

            if (touched.CameOnline)
                await AnnounceAsync(userId, isOnline: true, touched.At, lastSeenAt: null, step.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not record the presence of connection {ConnectionId} of user {UserId}; the next refresh will",
                connectionId, userId);
        }
    }

    public async Task DisconnectedAsync(Guid userId, string connectionId)
    {
        _connections.TryRemove(connectionId, out _);

        try
        {
            using var step = StepToken();

            // With no grace the user is taken offline here. With a grace period they are left in place, and
            // if this was their last connection a timer finishes the job later unless they are back.
            var settled = await _store.SettleAsync(
                userId, connectionId, finish: _options.OfflineGrace == TimeSpan.Zero, step.Token);

            switch (settled.Outcome)
            {
                case SettleOutcome.WentOffline:
                    await AnnounceAsync(userId, isOnline: false, settled.At, lastSeenAt: settled.At, step.Token);
                    break;

                case SettleOutcome.NoConnectionsLeft:
                    _ = FinishAfterGraceAsync(userId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not record that connection {ConnectionId} of user {UserId} closed; their entry will expire",
                connectionId, userId);
        }
    }

    // Not awaited by anyone, so it handles its own failures. If the server stops during the wait the user
    // stays in Redis until their entries expire and a sweep (on any server) takes them offline.
    private async Task FinishAfterGraceAsync(Guid userId)
    {
        try
        {
            await Task.Delay(_options.OfflineGrace, _lifetime.ApplicationStopping);

            using var step = StepToken();
            var settled = await _store.SettleAsync(userId, connectionId: null, finish: true, step.Token);

            if (settled.Outcome == SettleOutcome.WentOffline)
                await AnnounceAsync(userId, isOnline: false, settled.At, lastSeenAt: settled.At, step.Token);
        }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not take user {UserId} offline after the wait; their entries will expire", userId);
        }
    }

    // ##### Called by the refresh service #####

    // Renews the entry of every connection this server holds. A connection that finds its user not online
    // (their entries expired while this server was paused, or Redis lost its data) brings them back.
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var held = _connections.Select(c => (UserId: c.Value, ConnectionId: c.Key)).ToList();
        if (held.Count == 0)
            return;

        using var step = StepToken(cancellationToken);
        var results = await _store.TouchManyAsync(held, step.Token);

        for (var i = 0; i < held.Count; i++)
        {
            if (results[i].CameOnline)
                await AnnounceAsync(held[i].UserId, isOnline: true, results[i].At, lastSeenAt: null, step.Token);
        }
    }

    // Takes offline everyone whose entries all expired without being settled: a server that died took
    // their connections with it. Safe to run on every server at once: Redis lets exactly one of them
    // take a given user offline, and only that one announces it.
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var step = StepToken(cancellationToken);
            var expired = await _store.GetExpiredAsync(SweepBatchSize, step.Token);

            foreach (var user in expired.Users)
            {
                var settled = await _store.SettleAsync(user.UserId, connectionId: null, finish: true, step.Token);

                if (settled.Outcome == SettleOutcome.WentOffline)
                    await AnnounceAsync(user.UserId, isOnline: false, settled.At, user.LastActiveAt, step.Token);
            }

            if (expired.Users.Count < SweepBatchSize)
                return;
        }
    }

    // ##### Telling the contacts #####

    private async Task AnnounceAsync(
        Guid userId, bool isOnline, DateTime at, DateTime? lastSeenAt, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var contacts = await scope.ServiceProvider.GetRequiredService<IConversationRepository>()
            .GetContactIdsAsync(userId, cancellationToken);

        // Stored before anyone is told, so a client that reacts to the push by reading it finds it.
        if (lastSeenAt is { } seen)
            await scope.ServiceProvider.GetRequiredService<IPresenceRepository>()
                .SetLastSeenAsync(userId, seen, cancellationToken);

        _logger.LogInformation(
            "User {UserId} is {State}; telling {ContactCount} contacts", userId, isOnline ? "online" : "offline", contacts.Count);

        if (contacts.Count == 0)
            return;

        await _hub.Clients.Users(contacts.Select(c => c.ToString()).ToList())
            .PresenceChanged(new PresenceDto(userId, isOnline, lastSeenAt, at));
    }

    // A bound on each step, and a stop when the application is shutting down.
    private CancellationTokenSource StepToken(CancellationToken cancellationToken = default)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping, cancellationToken);
        source.CancelAfter(StepTimeout);
        return source;
    }
}
