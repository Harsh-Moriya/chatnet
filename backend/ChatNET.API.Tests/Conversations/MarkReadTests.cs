using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Conversations;

// Marking a conversation read moves the member's read position to a message ("I have read up to
// here"). The position only ever moves forward, so a late or repeated request cannot make read
// messages unread again. Everyone in the conversation is told, which is what read receipts show.
[Collection(IntegrationTestCollection.Name)]
public class MarkReadTests
{
    private readonly ChatApiFactory _factory;

    public MarkReadTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### What it does #####

    [Fact]
    public async Task Reading_up_to_the_newest_message_clears_the_count()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        await bobClient.SendMessageAsync(conversation, "one");
        var newest = await bobClient.SendMessageAsync(conversation, "two");

        var response = await aliceClient.MarkReadResponseAsync(conversation, newest.MessageIdOf());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await aliceClient.UnreadInListAsync(conversation));
    }

    [Fact]
    public async Task The_position_is_the_time_of_the_message_that_was_read()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "read me");

        await aliceClient.MarkReadAsync(conversation, sent.MessageIdOf());

        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), await _factory.LastReadAtAsync(conversation, alice.Id));
    }

    [Fact]
    public async Task Reading_an_older_message_leaves_the_later_ones_unread()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var first = await bobClient.SendMessageAsync(conversation, "one");
        await bobClient.SendMessageAsync(conversation, "two");
        await bobClient.SendMessageAsync(conversation, "three");

        await aliceClient.MarkReadAsync(conversation, first.MessageIdOf());

        Assert.Equal(2, await aliceClient.UnreadInListAsync(conversation));
    }

    [Fact]
    public async Task Reading_does_not_change_anyone_elses_position()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hello");

        await aliceClient.MarkReadAsync(conversation, sent.MessageIdOf());

        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), await _factory.LastReadAtAsync(conversation, alice.Id));
        // Bob's position is where sending his own message put it, not Alice's.
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), await _factory.LastReadAtAsync(conversation, bob.Id));
        Assert.Equal(0, await bobClient.UnreadInListAsync(conversation));
    }

    // ##### The position only moves forward #####

    [Fact]
    public async Task Reading_an_older_message_afterwards_does_not_move_the_position_back()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var older = await bobClient.SendMessageAsync(conversation, "older");
        var newer = await bobClient.SendMessageAsync(conversation, "newer");
        await aliceClient.MarkReadAsync(conversation, newer.MessageIdOf());

        await aliceClient.MarkReadAsync(conversation, older.MessageIdOf());

        Assert.Equal(newer.GetProperty("createdAt").GetDateTime(), await _factory.LastReadAtAsync(conversation, alice.Id));
        Assert.Equal(0, await aliceClient.UnreadInListAsync(conversation));
    }

    // Requests from two tabs, or a retry, can reach the server in any order and at the same moment.
    // The single UPDATE with its own "only if it moves forward" condition settles it in the database.
    [Fact]
    public async Task Concurrent_reads_in_any_order_end_at_the_newest_message()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        var messages = new List<JsonElement>();
        for (var i = 0; i < 8; i++)
            messages.Add(await bobClient.SendMessageAsync(conversation, $"message {i}"));
        var newest = messages[^1];
        var random = new Random(7);

        for (var round = 0; round < 12; round++)
        {
            await _factory.ExecuteDbAsync(db => db.ConversationMembers
                .Where(m => m.ConversationId == conversation && m.UserId == alice.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadAt, (DateTime?)null)));
            var shuffled = messages.OrderBy(_ => random.Next()).ToList();

            var results = await Task.WhenAll(shuffled.Select(async message =>
            {
                using var client = _factory.CreateClientFor(alice);
                return await client.MarkReadResponseAsync(conversation, message.MessageIdOf());
            }));

            Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
            Assert.Equal(newest.GetProperty("createdAt").GetDateTime(), await _factory.LastReadAtAsync(conversation, alice.Id));
        }
    }

    // ##### What may be read #####

    [Fact]
    public async Task A_message_that_does_not_exist_is_refused()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.MarkReadResponseAsync(conversation, Guid.NewGuid());

        await AssertUnknownMessageAsync(response);
    }

    [Fact]
    public async Task A_message_of_another_conversation_is_refused_the_same_way()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var withBob = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var withCarol = (await client.CreateDirectAsync(carol.Id)).IdOf();
        var elsewhere = await client.SendMessageAsync(withCarol, "in the other chat");

        var response = await client.MarkReadResponseAsync(withBob, elsewhere.MessageIdOf());

        await AssertUnknownMessageAsync(response);
        Assert.Null(await _factory.LastReadAtAsync(withBob, alice.Id));
    }

    [Fact]
    public async Task A_message_from_before_the_member_joined_is_refused_the_same_way()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Late joiner", bob.Id, carol.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);
        var joined = DateTime.UtcNow.AddMinutes(-30).TruncateToMicroseconds();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == carol.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, joined)));
        await _factory.AddMessageAsync(group, alice.Id, "before carol", joined.AddMinutes(-10));
        var hidden = (await aliceClient.GetHistoryAsync(group))[0].MessageIdOf();

        var response = await carolClient.MarkReadResponseAsync(group, hidden);

        await AssertUnknownMessageAsync(response);
    }

    [Fact]
    public async Task An_empty_message_id_or_body_is_a_bad_request()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var empty = await client.MarkReadResponseAsync(conversation, Guid.Empty);
        var noBody = await client.PostAsync($"/api/conversations/{conversation}/read", null);
        var notAGuid = await client.PostAsJsonAsync($"/api/conversations/{conversation}/read", new { messageId = "abc" });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAGuid.StatusCode);
    }

    // The status alone cannot tell the validator from the handler: both answer 400. The text can. An
    // empty id is refused by the validator before the handler runs, with the validator's own message
    // and the field it concerns, so a client can mark the field.
    [Fact]
    public async Task An_empty_message_id_is_refused_by_the_validator_and_names_the_field()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.MarkReadResponseAsync(conversation, Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Validation Failed", problem.Title);
        Assert.NotEqual("Unknown message.", problem.Detail);
        Assert.True(problem.Root.GetProperty("errors").TryGetProperty("MessageId", out _),
            "The validation error should be reported on the MessageId field.");
    }

    // ##### Who may read #####

    [Fact]
    public async Task A_non_member_is_refused_before_the_message_is_looked_at()
    {
        var (_, bob, conversation) = await DirectChatAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var bobClient = _factory.CreateClientFor(bob);
        using var intruderClient = _factory.CreateClientFor(intruder);
        var message = await bobClient.SendMessageAsync(conversation, "private");

        var real = await intruderClient.MarkReadResponseAsync(conversation, message.MessageIdOf());
        var unknown = await intruderClient.MarkReadResponseAsync(conversation, Guid.NewGuid());
        var noConversation = await intruderClient.MarkReadResponseAsync(Guid.NewGuid(), Guid.NewGuid());

        foreach (var response in new[] { real, unknown, noConversation })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("You are not a member of this conversation.", (await response.ReadProblemAsync()).Detail);
        }
    }

    [Fact]
    public async Task A_member_who_has_left_is_refused()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var group = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        var message = await aliceClient.SendMessageAsync(group, "while bob was here");
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await bobClient.MarkReadResponseAsync(group, message.MessageIdOf());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await _factory.LastReadAtAsync(group, bob.Id));
    }

    // The handler checks membership first, so the update's own condition is only reached when a member
    // leaves between that check and the update. The repository method is tested directly for it.
    [Fact]
    public async Task The_position_of_a_member_who_has_left_is_not_moved_by_the_repository()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        var group = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        using var scope = _factory.Services.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();

        var moved = await conversations.AdvanceLastReadAsync(group, bob.Id, DateTime.UtcNow, CancellationToken.None);

        Assert.False(moved);
        Assert.Null(await _factory.LastReadAtAsync(group, bob.Id));
    }

    [Fact]
    public async Task Marking_read_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.MarkReadResponseAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### Telling the others #####

    [Fact]
    public async Task Every_member_is_told_who_read_up_to_when()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var group = (await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();
        var sent = await aliceClient.SendMessageAsync(group, "read this");
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);
        await using var carolTab = await HubClient.ConnectAsync(_factory, carol);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);

        await bobClient.MarkReadAsync(group, sent.MessageIdOf());

        foreach (var tab in new[] { aliceTab, carolTab, bobTab })
        {
            var read = await tab.WaitForReadAsync(r => r.ConversationId == group);
            Assert.Equal(bob.Id, read.UserId);
            Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), read.ReadAt);
        }
    }

    [Fact]
    public async Task The_readers_other_tabs_are_told_too()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hi");
        await using var readingTab = await HubClient.ConnectAsync(_factory, alice);
        await using var otherTab = await HubClient.ConnectAsync(_factory, alice);

        await aliceClient.MarkReadAsync(conversation, sent.MessageIdOf());

        await readingTab.WaitForReadAsync(r => r.UserId == alice.Id);
        await otherTab.WaitForReadAsync(r => r.UserId == alice.Id);
    }

    [Fact]
    public async Task Nobody_outside_the_conversation_is_told()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hi");
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, outsider);

        await aliceClient.MarkReadAsync(conversation, sent.MessageIdOf());

        Assert.False(await outsiderTab.ReceivedReadAsync(_ => true));
    }

    [Fact]
    public async Task A_read_that_changes_nothing_tells_nobody()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var older = await bobClient.SendMessageAsync(conversation, "older");
        var newer = await bobClient.SendMessageAsync(conversation, "newer");
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        await aliceClient.MarkReadAsync(conversation, newer.MessageIdOf());
        await bobTab.WaitForReadAsync(r => r.UserId == alice.Id);

        await aliceClient.MarkReadAsync(conversation, newer.MessageIdOf());
        await aliceClient.MarkReadAsync(conversation, older.MessageIdOf());

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        Assert.Single(bobTab.ReadEvents, r => r.UserId == alice.Id);
    }

    [Fact]
    public async Task A_refused_read_tells_nobody()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var intruderClient = _factory.CreateClientFor(intruder);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hi");
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);

        await intruderClient.MarkReadResponseAsync(conversation, sent.MessageIdOf());
        await bobClient.MarkReadResponseAsync(conversation, Guid.NewGuid());

        Assert.False(await aliceTab.ReceivedReadAsync(_ => true));
    }

    // Clients treat the push as the truth, so the new position must already be stored by the time
    // anyone hears about it.
    [Fact]
    public async Task The_position_is_stored_before_anyone_is_told()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hi");
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);

        await aliceClient.MarkReadAsync(conversation, sent.MessageIdOf());

        var read = await bobTab.WaitForReadAsync(r => r.UserId == alice.Id);
        Assert.Equal(read.ReadAt, await _factory.LastReadAtAsync(conversation, alice.Id));
    }

    [Fact]
    public async Task Members_on_another_server_instance_are_told_too()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hi");
        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await using var bobTab = await HubClient.ConnectAsync(otherInstance, bob);

        await aliceClient.MarkReadAsync(conversation, sent.MessageIdOf());

        await bobTab.WaitForReadAsync(r => r.UserId == alice.Id);
    }

    // The position is saved before the push is attempted, so a failing push (Redis down) must not
    // turn a successful request into an error. Others see the new position on their next load.
    [Fact]
    public async Task A_failing_push_does_not_fail_the_request_and_is_logged_with_ids_only()
    {
        var capture = new CapturingLoggerProvider();
        var (alice, bob, conversation) = await DirectChatAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        var secret = $"secret-{Guid.NewGuid():N}";
        var sent = await bobClient.SendMessageAsync(conversation, secret);
        using var brokenHub = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
            services.AddLogging(logging => logging.AddProvider(capture));
        }));
        using var client = brokenHub.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);

        var response = await client.MarkReadResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), await _factory.LastReadAtAsync(conversation, alice.Id));
        Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains(conversation.ToString()));
        Assert.DoesNotContain(capture.Entries, e => e.Contains(secret));
    }

    // ##### Helpers #####

    private static async Task AssertUnknownMessageAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Validation Failed", problem.Title);
        Assert.Equal("Unknown message.", problem.Detail);
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid ConversationId)> DirectChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(conversation);
        return (alice, bob, conversation);
    }
}
