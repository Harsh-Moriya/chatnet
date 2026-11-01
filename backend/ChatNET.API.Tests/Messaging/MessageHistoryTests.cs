using System.Net;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Common.Time;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// History is what a client loads when it opens a conversation: the latest messages, oldest first,
// so the list can be rendered top to bottom. Live messages then arrive over SignalR.
[Collection(IntegrationTestCollection.Name)]
public class MessageHistoryTests
{
    private const string NotAMember = "You are not a member of this conversation.";

    private readonly ChatApiFactory _factory;

    public MessageHistoryTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### What is returned #####

    [Fact]
    public async Task A_new_conversation_has_an_empty_history()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var history = await client.GetHistoryAsync(conversation);

        Assert.Empty(history);
    }

    [Fact]
    public async Task Messages_come_back_oldest_first_with_their_sender_names()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        var start = DateTime.UtcNow.AddHours(-1);
        await _factory.AddMessageAsync(conversation, bob.Id, "third", start.AddMinutes(3));
        await _factory.AddMessageAsync(conversation, alice.Id, "first", start.AddMinutes(1));
        await _factory.AddMessageAsync(conversation, bob.Id, "second", start.AddMinutes(2));
        using var client = _factory.CreateClientFor(alice);

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal(["first", "second", "third"], history.Select(m => m.ContentOf()));
        Assert.Equal(
            [alice.DisplayName, bob.DisplayName, bob.DisplayName],
            history.Select(m => m.GetProperty("senderDisplayName").GetString()));
        Assert.Equal([alice.Id, bob.Id, bob.Id], history.Select(m => m.GetProperty("senderId").GetGuid()));
    }

    [Fact]
    public async Task Only_the_latest_50_are_returned_still_oldest_first()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, 60, DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(1));
        using var client = _factory.CreateClientFor(alice);

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal(50, history.Count);
        Assert.Equal(
            Enumerable.Range(10, 50).Select(i => $"message {i}"),
            history.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task Exactly_50_messages_are_all_returned()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, 50, DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(1));
        using var client = _factory.CreateClientFor(alice);

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal(50, history.Count);
        Assert.Equal("message 0", history[0].ContentOf());
    }

    [Fact]
    public async Task Only_the_requested_conversations_messages_are_returned()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var withBob = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var withCarol = (await client.CreateDirectAsync(carol.Id)).IdOf();
        await _factory.BackdateJoinAsync(withBob);
        await _factory.BackdateJoinAsync(withCarol);
        await _factory.AddMessageAsync(withBob, bob.Id, "for the Bob chat", DateTime.UtcNow.AddMinutes(-2));
        await _factory.AddMessageAsync(withCarol, carol.Id, "for the Carol chat", DateTime.UtcNow.AddMinutes(-1));

        var history = await client.GetHistoryAsync(withBob);

        Assert.Equal(["for the Bob chat"], history.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task Every_member_sees_the_same_history()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();
        await aliceClient.SendMessageAsync(group, "one");
        await bobClient.SendMessageAsync(group, "two");

        var forAlice = await aliceClient.GetHistoryAsync(group);
        var forCarol = await carolClient.GetHistoryAsync(group);

        Assert.Equal(["one", "two"], forAlice.Select(m => m.ContentOf()));
        Assert.Equal(forAlice.Select(m => m.GetRawText()), forCarol.Select(m => m.GetRawText()));
    }

    [Fact]
    public async Task A_message_that_was_just_sent_is_in_the_history_exactly_as_it_was_returned_to_the_sender()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();

        var sent = await client.SendMessageAsync(conversation, "Round trip", clientMessageId);
        var history = await client.GetHistoryAsync(conversation);

        var loaded = Assert.Single(history);
        Assert.Equal(sent.MessageIdOf(), loaded.MessageIdOf());
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), loaded.GetProperty("createdAt").GetDateTime());
        // The client's temporary ID is stored with the message, so history returns it as the send did.
        Assert.Equal(clientMessageId, loaded.GetProperty("clientMessageId").GetGuid());
        Assert.Equal(clientMessageId, sent.GetProperty("clientMessageId").GetGuid());
    }

    // The timeline keeps its shape when a message is deleted: the row stays and only the content goes.
    [Fact]
    public async Task A_deleted_message_stays_in_place_without_its_content()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        var start = DateTime.UtcNow.AddHours(-1);
        await _factory.AddMessageAsync(conversation, bob.Id, "before", start);
        await _factory.AddMessageAsync(conversation, bob.Id, "secret that was deleted", start.AddMinutes(1), deleted: true);
        await _factory.AddMessageAsync(conversation, bob.Id, "after", start.AddMinutes(2));
        using var client = _factory.CreateClientFor(alice);

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal(["before", null, "after"], history.Select(m => m.ContentOf()));
        Assert.DoesNotContain("secret that was deleted", JsonSerializer.Serialize(history));
    }

    [Fact]
    public async Task The_message_has_the_documented_fields()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "Shape check");

        var message = Assert.Single(await client.GetHistoryAsync(conversation));

        Assert.Equivalent(
            new[] { "id", "conversationId", "senderId", "senderDisplayName", "content", "createdAt", "clientMessageId" },
            message.EnumerateObject().Select(p => p.Name));
    }

    // ##### Which messages a member may see #####

    // Joining a conversation does not reveal what was said before. JoinedAt is the lower bound.
    [Fact]
    public async Task A_member_sees_only_messages_from_when_they_joined()
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
        await _factory.AddMessageAsync(group, alice.Id, "just before", joined.AddTicks(-10));
        await _factory.AddMessageAsync(group, alice.Id, "exactly when", joined);
        await _factory.AddMessageAsync(group, alice.Id, "after carol", joined.AddMinutes(10));

        var forCarol = await carolClient.GetHistoryAsync(group);
        var forAlice = await aliceClient.GetHistoryAsync(group);

        Assert.Equal(["exactly when", "after carol"], forCarol.Select(m => m.ContentOf()));
        Assert.Equal(4, forAlice.Count);
    }

    [Fact]
    public async Task A_non_member_is_refused()
    {
        var (_, _, conversation) = await DirectChatAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var intruderClient = _factory.CreateClientFor(intruder);

        var response = await intruderClient.GetHistoryResponseAsync(conversation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Forbidden", problem.Title);
        Assert.Equal(NotAMember, problem.Detail);
    }

    [Fact]
    public async Task An_unknown_conversation_gets_the_same_refusal_as_one_the_user_is_not_in()
    {
        var (_, _, existing) = await DirectChatAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var intruderClient = _factory.CreateClientFor(intruder);

        var forExisting = await intruderClient.GetHistoryResponseAsync(existing);
        var forUnknown = await intruderClient.GetHistoryResponseAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, forExisting.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forUnknown.StatusCode);
        Assert.Equal((await forExisting.ReadProblemAsync()).Detail, (await forUnknown.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_member_who_has_left_reads_the_history_up_to_when_they_left()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var group = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await aliceClient.SendMessageAsync(group, "while bob was here");
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await bobClient.GetHistoryResponseAsync(group);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["while bob was here"], (await bobClient.GetHistoryAsync(group)).Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task History_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetHistoryResponseAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await response.ReadProblemAsync()).Status);
    }

    [Fact]
    public async Task A_conversation_id_that_is_not_a_guid_does_not_match_the_route()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync("/api/conversations/not-a-guid/messages");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, (await response.ReadProblemAsync()).Status);
    }

    // ##### Cost #####

    // A membership lookup, the messages query and one query for the files of the whole page, whatever the number of
    // messages, senders or files.
    [Fact]
    public async Task Loading_history_uses_a_fixed_number_of_queries()
    {
        var counter = new QueryCounter();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(counter))));
        var alice = await _factory.RegisterUserAsync("alice");
        var members = new List<TestUser>();
        for (var i = 0; i < 6; i++)
            members.Add(await _factory.RegisterUserAsync("member"));
        using var setup = _factory.CreateClientFor(alice);
        var group = (await setup.CreateGroupAsync("Many senders", members.Select(m => m.Id).ToArray())).IdOf();
        await _factory.BackdateJoinAsync(group);
        foreach (var member in members)
            await _factory.AddMessagesAsync(group, member.Id, 10, DateTime.UtcNow.AddHours(-1), TimeSpan.FromSeconds(1), member.UserName);

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        counter.Reset();
        var history = await client.GetHistoryAsync(group);

        Assert.Equal(50, history.Count);
        Assert.True(counter.Count <= 3, $"Loading history ran {counter.Count} queries; expected at most 3.");
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
