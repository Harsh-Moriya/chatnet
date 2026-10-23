using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChatNET.API.Tests.Infrastructure;

public sealed record TypingEvent(Guid ConversationId, Guid UserId, string DisplayName, bool IsTyping);

public sealed record ReadEvent(Guid ConversationId, Guid UserId, DateTime ReadAt);

public sealed record MuteEvent(Guid ConversationId, bool IsMuted);

// A real SignalR client talking to the in-memory test server over WebSockets, as a browser would:
// it negotiates over HTTP and then upgrades to a WebSocket, passing the token in the query string.
// Every event the server pushes is recorded so a test can wait for it, or check that none came.
public sealed class HubClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // How long a test listens before concluding that an event was not sent. Short on purpose:
    // delivery inside the test server takes a few milliseconds.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    private readonly HubConnection _connection;
    private readonly ConcurrentQueue<JsonElement> _messages = new();
    private readonly ConcurrentQueue<TypingEvent> _typing = new();
    private readonly ConcurrentQueue<JsonElement> _added = new();
    private readonly ConcurrentQueue<ReadEvent> _reads = new();
    private readonly ConcurrentQueue<JsonElement> _presence = new();
    private readonly ConcurrentQueue<MuteEvent> _muteChanges = new();

    private HubClient(HubConnection connection)
    {
        _connection = connection;

        // Payloads are kept as raw JSON so tests assert on what a client really receives.
        _connection.On<JsonElement>("ReceiveMessage", message => _messages.Enqueue(message));
        _connection.On<Guid, Guid, string, bool>("UserTyping", (conversationId, userId, displayName, isTyping) =>
            _typing.Enqueue(new TypingEvent(conversationId, userId, displayName, isTyping)));
        _connection.On<JsonElement>("AddedToConversation", conversation => _added.Enqueue(conversation));
        _connection.On<Guid, Guid, DateTime>("MessageRead", (conversationId, userId, readAt) =>
            _reads.Enqueue(new ReadEvent(conversationId, userId, readAt)));
        _connection.On<JsonElement>("PresenceChanged", presence => _presence.Enqueue(presence));
        _connection.On<Guid, bool>("ConversationMuteChanged", (conversationId, isMuted) =>
            _muteChanges.Enqueue(new MuteEvent(conversationId, isMuted)));
    }

    public HubConnectionState State => _connection.State;

    public IReadOnlyList<JsonElement> Messages => _messages.ToArray();

    public IReadOnlyList<TypingEvent> TypingEvents => _typing.ToArray();

    public IReadOnlyList<JsonElement> AddedToConversation => _added.ToArray();

    public IReadOnlyList<ReadEvent> ReadEvents => _reads.ToArray();

    public IReadOnlyList<JsonElement> PresenceEvents => _presence.ToArray();

    public IReadOnlyList<MuteEvent> MuteChanges => _muteChanges.ToArray();

    public static Task<HubClient> ConnectAsync(WebApplicationFactory<Program> host, TestUser user) =>
        ConnectAsync(host, user.Token);

    // A null token connects without credentials, which the server must refuse.
    public static async Task<HubClient> ConnectAsync(WebApplicationFactory<Program> host, string? token)
    {
        var server = host.Server;

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/chat"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                // A browser cannot set headers on a WebSocket upgrade, so the JavaScript client
                // appends the token to the URL. The same is done here, which exercises the
                // server's query string token handling instead of a header the browser never sends.
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await server.CreateWebSocketClient().ConnectAsync(
                        token is null ? context.Uri : WithAccessToken(context.Uri, token), cancellationToken);

                if (token is not null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        var client = new HubClient(connection);
        await connection.StartAsync();

        // StartAsync can return before the server has finished OnConnectedAsync (the handshake is
        // answered first), so a test that pushed straight away could beat the group joins. The
        // server handles a connection's invocations only after OnConnectedAsync completes, so one
        // round trip is a reliable "ready" signal. Stopping typing in a conversation nobody is in
        // is a harmless no-op, which makes it a suitable ping. A connection the server refuses
        // never gets this far.
        await connection.InvokeAsync("StopTyping", Guid.Empty);

        return client;
    }

    // InvokeAsync waits for the hub method to finish, so a HubException thrown by the server
    // surfaces here as an exception.
    public Task JoinConversationAsync(Guid conversationId) => _connection.InvokeAsync("JoinConversation", conversationId);

    public Task StartTypingAsync(Guid conversationId) => _connection.InvokeAsync("StartTyping", conversationId);

    public Task StopTypingAsync(Guid conversationId) => _connection.InvokeAsync("StopTyping", conversationId);

    public Task StopAsync() => _connection.StopAsync();

    // True if the server (or the network) closed the connection within the timeout. The test
    // connection has no automatic reconnect, so a close is final and reported at once.
    public async Task<bool> WaitForCloseAsync(TimeSpan timeout)
    {
        var closed = new TaskCompletionSource();
        _connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        if (_connection.State == HubConnectionState.Disconnected)
            return true;

        return await Task.WhenAny(closed.Task, Task.Delay(timeout)) == closed.Task;
    }

    public Task<JsonElement> WaitForMessageAsync(Guid conversationId, TimeSpan? timeout = null) =>
        WaitAsync(_messages, m => m.GetProperty("conversationId").GetGuid() == conversationId, timeout);

    public Task<JsonElement> WaitForMessageAsync(Guid conversationId, Guid messageId, TimeSpan? timeout = null) =>
        WaitAsync(_messages, m => m.GetProperty("conversationId").GetGuid() == conversationId
                                  && m.GetProperty("id").GetGuid() == messageId, timeout);

    public Task<TypingEvent> WaitForTypingAsync(Func<TypingEvent, bool> match, TimeSpan? timeout = null) =>
        WaitAsync(_typing, match, timeout);

    public Task<JsonElement> WaitForAddedAsync(Guid conversationId, TimeSpan? timeout = null) =>
        WaitAsync(_added, c => c.GetProperty("id").GetGuid() == conversationId, timeout);

    public Task<ReadEvent> WaitForReadAsync(Func<ReadEvent, bool> match, TimeSpan? timeout = null) =>
        WaitAsync(_reads, match, timeout);

    // The next presence change about this user (online or offline, as asked).
    public Task<JsonElement> WaitForPresenceAsync(Guid userId, bool isOnline, TimeSpan? timeout = null) =>
        WaitAsync(_presence, p => p.GetProperty("userId").GetGuid() == userId
                                  && p.GetProperty("isOnline").GetBoolean() == isOnline, timeout);

    // Listens for a short while and reports whether any matching event arrived. Used to prove an
    // event was NOT delivered; the quiet period keeps such tests fast but not instant.
    public async Task<bool> ReceivedMessageAsync(Guid conversationId)
    {
        await Task.Delay(QuietPeriod);
        return _messages.Any(m => m.GetProperty("conversationId").GetGuid() == conversationId);
    }

    public async Task<bool> ReceivedTypingAsync(Func<TypingEvent, bool> match)
    {
        await Task.Delay(QuietPeriod);
        return _typing.Any(match);
    }

    public async Task<bool> ReceivedAddedAsync(Guid conversationId)
    {
        await Task.Delay(QuietPeriod);
        return _added.Any(c => c.GetProperty("id").GetGuid() == conversationId);
    }

    public async Task<bool> ReceivedReadAsync(Func<ReadEvent, bool> match)
    {
        await Task.Delay(QuietPeriod);
        return _reads.Any(match);
    }

    public Task<MuteEvent> WaitForMuteChangedAsync(Func<MuteEvent, bool> match, TimeSpan? timeout = null) =>
        WaitAsync(_muteChanges, match, timeout);

    public async Task<bool> ReceivedMuteChangedAsync()
    {
        await Task.Delay(QuietPeriod);
        return !_muteChanges.IsEmpty;
    }

    // Listens for a short while and reports whether any presence change about this user arrived.
    public async Task<bool> ReceivedPresenceAsync(Guid userId)
    {
        await Task.Delay(QuietPeriod);
        return _presence.Any(p => p.GetProperty("userId").GetGuid() == userId);
    }

    // The same, for a change to offline only.
    public async Task<bool> ReceivedPresenceOfflineAsync(Guid userId)
    {
        await Task.Delay(QuietPeriod);
        return _presence.Any(p => p.GetProperty("userId").GetGuid() == userId
                                  && !p.GetProperty("isOnline").GetBoolean());
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static Uri WithAccessToken(Uri uri, string token)
    {
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = (existing.Length > 0 ? existing + "&" : string.Empty)
                        + "access_token=" + Uri.EscapeDataString(token);
        return builder.Uri;
    }

    private static async Task<T> WaitAsync<T>(ConcurrentQueue<T> queue, Func<T, bool> match, TimeSpan? timeout)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (DateTime.UtcNow < deadline)
        {
            foreach (var item in queue)
                if (match(item))
                    return item;

            await Task.Delay(20);
        }

        throw new TimeoutException($"No matching {typeof(T).Name} event arrived within {timeout ?? DefaultTimeout}.");
    }
}
