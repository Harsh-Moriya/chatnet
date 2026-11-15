using System.Net;
using System.Text.Json;
using ChatNET.API.Calls;
using ChatNET.API.Calls.Models;
using ChatNET.API.Common.Time;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Calls;

// A call that has ended is one row in the conversation, written by the server: it lands at the bottom, takes a change number like any
// other change, counts as unread only when it was missed, cannot be edited or deleted, reaches the conversation live, and alerts the
// callee when they are away only when it was missed.
[Collection(IntegrationTestCollection.Name)]
public class CallLogTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public CallLogTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Rows other tests left waiting would be taken by the relay passes these tests run, and the browsers they registered would be alerted.
    public async Task InitializeAsync()
    {
        await _factory.RemoveWaitingOutboxRowsAsync();
        await _factory.RemoveSubscriptionsAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.RemoveWaitingOutboxRowsAsync();
        await _factory.RemoveSubscriptionsAsync();
    }

    // A call that ended a moment ago and rang (or ran) for twenty seconds. A call that was answered was answered after five.
    private static Call Ended(
        Guid conversation, TestUser caller, TestUser callee, CallEndReason reason, bool answered = false, CallMedia media = CallMedia.Audio)
    {
        var endedAt = DateTime.UtcNow.TruncateToMicroseconds();
        var startedAt = endedAt.AddSeconds(-20);

        return new Call
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation,
            CallerId = caller.Id,
            CalleeId = callee.Id,
            Media = media,
            StartedAt = startedAt,
            AnsweredAt = answered ? startedAt.AddSeconds(5) : null,
            EndedAt = endedAt,
            EndReason = reason,
        };
    }

    private static async Task<bool> RecordAsync(IServiceProvider services, Call call)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CallLog>().RecordAsync(call, CancellationToken.None);
    }

    private Task<bool> RecordAsync(Call call) => RecordAsync(_factory.Services, call);

    // ##### The row #####

    [Fact]
    public async Task A_missed_call_is_a_row_after_what_was_said_that_carries_the_call_and_no_text()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(bob);
        var said = await client.SendMessageAsync(conversation, "are you there?");
        var call = Ended(conversation, alice, bob, CallEndReason.Missed, media: CallMedia.Video);

        Assert.True(await RecordAsync(call));

        var history = await client.GetHistoryAsync(conversation);
        Assert.Equal([said.MessageIdOf(), history[1].MessageIdOf()], history.Select(m => m.MessageIdOf()));
        var row = history[1];
        Assert.Equal("Call", row.GetProperty("kind").GetString());
        Assert.Equal(alice.Id, row.GetProperty("senderId").GetGuid());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("ciphertext").ValueKind);
        Assert.False(row.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(0, row.GetProperty("keyEpoch").GetInt32());
        Assert.Empty(row.GetProperty("attachments").EnumerateArray());

        var shown = row.GetProperty("call");
        Assert.Equal(call.Id, shown.GetProperty("id").GetGuid());
        Assert.Equal("Video", shown.GetProperty("media").GetString());
        Assert.Equal("Missed", shown.GetProperty("endReason").GetString());
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("answeredAt").ValueKind);
        Assert.True(shown.GetProperty("isMissed").GetBoolean());
        Assert.Equal(call.EndedAt, shown.GetProperty("endedAt").GetDateTime());
        Assert.Equal(call.EndedAt, row.GetProperty("createdAt").GetDateTime());
    }

    [Fact]
    public async Task An_ordinary_message_says_it_is_text_and_has_no_call()
    {
        var (_, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(bob);

        var said = await client.SendMessageAsync(conversation, "hello");

        Assert.Equal("Text", said.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, said.GetProperty("call").ValueKind);
    }

    [Fact]
    public async Task The_row_takes_a_change_number_and_is_in_the_changes_feed()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(bob);
        var said = await client.SendMessageAsync(conversation, "hello");
        var call = Ended(conversation, alice, bob, CallEndReason.Completed, answered: true);

        await RecordAsync(call);

        var changes = await client.ReadAllChangesAsync(conversation, since: said.ChangeSeqOf());
        var row = Assert.Single(changes);
        Assert.Equal(said.ChangeSeqOf() + 1, row.ChangeSeqOf());
        Assert.Equal("Call", row.GetProperty("kind").GetString());
        Assert.Equal(call.Id, row.GetProperty("call").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Recording_a_call_twice_writes_it_once()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var call = Ended(conversation, alice, bob, CallEndReason.Missed);

        Assert.True(await RecordAsync(call));
        Assert.False(await RecordAsync(call));

        Assert.Equal((1, 1), await RowsOfAsync(call));
    }

    [Fact]
    public async Task Two_instances_that_see_one_call_end_at_the_same_moment_write_it_once()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var call = Ended(conversation, alice, bob, CallEndReason.Completed, answered: true);

        var written = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RecordAsync(Copy(call))));

        Assert.Equal(1, written.Count(w => w));
        Assert.Equal((1, 1), await RowsOfAsync(call));
    }

    // Each recorder has an entity of its own, as two instances would.
    private static Call Copy(Call call) => new()
    {
        Id = call.Id, ConversationId = call.ConversationId, CallerId = call.CallerId, CalleeId = call.CalleeId, Media = call.Media,
        StartedAt = call.StartedAt, AnsweredAt = call.AnsweredAt, EndedAt = call.EndedAt, EndReason = call.EndReason,
    };

    // How many rows of the call exist: its own, and its row in the timeline.
    private Task<(int Calls, int Rows)> RowsOfAsync(Call call) => _factory.QueryDbAsync(async db =>
        (await db.Calls.CountAsync(c => c.Id == call.Id), await db.Messages.CountAsync(m => m.CallId == call.Id)));

    // ##### Unread and the list #####

    [Theory]
    [InlineData(CallEndReason.Missed, false, 1)]
    [InlineData(CallEndReason.Cancelled, false, 1)]
    [InlineData(CallEndReason.Busy, false, 1)]
    [InlineData(CallEndReason.Failed, false, 1)]
    [InlineData(CallEndReason.Declined, false, 0)]
    [InlineData(CallEndReason.Completed, true, 0)]
    [InlineData(CallEndReason.Failed, true, 0)]
    public async Task A_call_counts_as_unread_for_the_callee_only_when_it_was_missed(CallEndReason reason, bool answered, int unread)
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();

        await RecordAsync(Ended(conversation, alice, bob, reason, answered));

        using var callee = _factory.CreateClientFor(bob);
        using var caller = _factory.CreateClientFor(alice);
        Assert.Equal(unread, UnreadOf(await callee.ListConversationsAsync(), conversation));
        Assert.Equal(0, UnreadOf(await caller.ListConversationsAsync(), conversation));
    }

    private static int UnreadOf(List<JsonElement> list, Guid conversation) =>
        list.Single(c => c.IdOf() == conversation).GetProperty("unreadCount").GetInt32();

    [Fact]
    public async Task The_list_shows_a_call_as_the_last_message_of_the_conversation()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var call = Ended(conversation, alice, bob, CallEndReason.Declined, media: CallMedia.Video);
        await RecordAsync(call);
        using var client = _factory.CreateClientFor(bob);

        var last = (await client.ListConversationsAsync()).Single(c => c.IdOf() == conversation).GetProperty("lastMessage");

        Assert.Equal("Call", last.GetProperty("kind").GetString());
        Assert.Equal(alice.Id, last.GetProperty("senderId").GetGuid());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("ciphertext").ValueKind);
        Assert.Equal(call.Id, last.GetProperty("call").GetProperty("id").GetGuid());
        Assert.Equal("Declined", last.GetProperty("call").GetProperty("endReason").GetString());
    }

    [Fact]
    public async Task A_call_never_moves_the_read_position_of_anyone()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(bob);
        await client.SendMessageAsync(conversation, "hello");
        var bobBefore = await ReadAtAsync(conversation, bob);
        var aliceBefore = await ReadAtAsync(conversation, alice);

        await RecordAsync(Ended(conversation, alice, bob, CallEndReason.Completed, answered: true));

        Assert.Equal(bobBefore, await ReadAtAsync(conversation, bob));
        Assert.Equal(aliceBefore, await ReadAtAsync(conversation, alice));
    }

    private Task<DateTime?> ReadAtAsync(Guid conversation, TestUser user) => _factory.QueryDbAsync(db =>
        db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == conversation && m.UserId == user.Id)
            .Select(m => m.LastReadAt).SingleAsync());

    // ##### Nobody changes the row #####

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nobody_can_edit_or_delete_the_row_of_a_call(bool theCaller)
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await RecordAsync(Ended(conversation, alice, bob, CallEndReason.Missed));
        using var client = _factory.CreateClientFor(theCaller ? alice : bob);
        var row = (await client.GetHistoryAsync(conversation)).Single();

        var edit = await client.PatchMessageAsync(conversation, row.MessageIdOf(), "not a call any more", editCount: 1, epoch: 1);
        var delete = await client.DeleteMessageResponseAsync(conversation, row.MessageIdOf());

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("call-row", (await edit.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("call-row", (await delete.ReadProblemAsync()).Code);
        Assert.Equal("Call", (await client.GetHistoryAsync(conversation)).Single().GetProperty("kind").GetString());
    }

    // ##### The conversation hears of it #####

    [Fact]
    public async Task The_other_member_is_sent_the_row_when_the_call_ends()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var hub = await HubClient.ConnectAsync(_factory, bob);
        var call = Ended(conversation, alice, bob, CallEndReason.Missed);

        await RecordAsync(call);

        var pushed = await hub.WaitForMessageAsync(conversation);
        Assert.Equal("Call", pushed.GetProperty("kind").GetString());
        Assert.Equal(call.Id, pushed.GetProperty("call").GetProperty("id").GetGuid());
        Assert.Equal(alice.DisplayName, pushed.GetProperty("senderDisplayName").GetString());
    }

    // ##### The alert #####

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host, FakePushService Service) PushHost()
    {
        var service = new FakePushService();
        var host = _factory.WithPush(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<IPushSender, WebPushSender>().ConfigurePrimaryHttpMessageHandler(() => service)));
        return (host, service);
    }

    [Fact]
    public async Task A_missed_call_alerts_the_callee_when_they_are_away_in_words_that_name_the_call()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(bob.Id, browser);
        var (host, service) = PushHost();
        using var _ = host;
        var call = Ended(conversation, alice, bob, CallEndReason.Missed, media: CallMedia.Video);

        await RecordAsync(host.Services, call);
        await host.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(service.Received);
        var alert = push.AlertFor(browser);
        Assert.Equal(alice.DisplayName, alert.GetProperty("title").GetString());
        Assert.Equal("Missed video call", alert.GetProperty("body").GetString());
        Assert.Equal("missed-call", alert.GetProperty("type").GetString());
        Assert.Equal($"call-{call.Id}", alert.GetProperty("tag").GetString());
        Assert.Equal(conversation, alert.GetProperty("conversationId").GetGuid());
    }

    [Theory]
    [InlineData(CallEndReason.Completed, true)]
    [InlineData(CallEndReason.Declined, false)]
    public async Task A_call_that_was_not_missed_saves_no_alert_and_alerts_nobody(CallEndReason reason, bool answered)
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(bob.Id, browser);
        var (host, service) = PushHost();
        using var _ = host;
        var call = Ended(conversation, alice, bob, reason, answered);

        await RecordAsync(host.Services, call);
        await host.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        var message = await _factory.QueryDbAsync(db => db.Messages.AsNoTracking().SingleAsync(m => m.CallId == call.Id));
        Assert.Equal(["MessageCreated"], (await _factory.OutboxRowsAboutAsync("messageId", message.Id)).Select(r => r.Kind));
        Assert.Empty(service.Received);
    }
}
