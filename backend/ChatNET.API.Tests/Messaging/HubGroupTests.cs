using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// A SignalR group is a named set of connections that can be messaged together. The hub puts each
// connection into the group of every conversation its user belongs to, so a push to
// "conversation X" reaches exactly the members who are online. These tests push to a group the way
// the messaging code will and check who receives it.
[Collection(IntegrationTestCollection.Name)]
public class HubGroupTests
{
    private const string NotAMember = "You are not a member of this conversation.";

    private readonly ChatApiFactory _factory;

    public HubGroupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task On_connect_the_user_joins_the_group_of_every_conversation_they_belong_to()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceApi = _factory.CreateClientFor(alice);
        using var bobApi = _factory.CreateClientFor(bob);
        var withBob = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        var team = (await aliceApi.CreateGroupAsync("Team", carol.Id)).IdOf();
        var bobAndCarol = (await bobApi.CreateDirectAsync(carol.Id)).IdOf();

        await using var client = await HubClient.ConnectAsync(_factory, alice);
        await PushAsync(_factory, withBob);
        await PushAsync(_factory, team);
        await PushAsync(_factory, bobAndCarol);

        await client.WaitForMessageAsync(withBob);
        await client.WaitForMessageAsync(team);
        Assert.False(await client.ReceivedMessageAsync(bobAndCarol));
    }

    [Fact]
    public async Task Every_tab_of_a_user_joins_the_groups()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();

        await using var tab1 = await HubClient.ConnectAsync(_factory, alice);
        await using var tab2 = await HubClient.ConnectAsync(_factory, alice);
        await PushAsync(_factory, conversation);

        await tab1.WaitForMessageAsync(conversation);
        await tab2.WaitForMessageAsync(conversation);
    }

    [Fact]
    public async Task A_conversation_the_user_has_left_is_not_joined()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var group = (await aliceApi.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await PushAsync(_factory, group);

        await aliceClient.WaitForMessageAsync(group);
        Assert.False(await bobClient.ReceivedMessageAsync(group));
    }

    [Fact]
    public async Task A_conversation_created_after_connecting_is_not_joined_until_the_client_asks()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var client = await HubClient.ConnectAsync(_factory, alice);
        using var aliceApi = _factory.CreateClientFor(alice);

        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await PushAsync(_factory, conversation);
        Assert.False(await client.ReceivedMessageAsync(conversation));

        await client.JoinConversationAsync(conversation);
        await PushAsync(_factory, conversation);

        await client.WaitForMessageAsync(conversation);
    }

    [Fact]
    public async Task Joining_a_conversation_the_user_is_not_in_is_refused_and_grants_nothing()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var aliceApi = _factory.CreateClientFor(alice);
        var private1 = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await using var client = await HubClient.ConnectAsync(_factory, intruder);

        var failure = await Record.ExceptionAsync(() => client.JoinConversationAsync(private1));
        await PushAsync(_factory, private1);

        Assert.NotNull(failure);
        Assert.Contains(NotAMember, failure.Message);
        Assert.False(await client.ReceivedMessageAsync(private1));
    }

    // A refusal that differed for "does not exist" and "exists but not yours" would let anyone
    // probe which conversation IDs are real.
    [Fact]
    public async Task An_unknown_conversation_is_refused_with_the_same_message_as_one_the_user_is_not_in()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var aliceApi = _factory.CreateClientFor(alice);
        var existing = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await using var client = await HubClient.ConnectAsync(_factory, intruder);

        var forExisting = await Record.ExceptionAsync(() => client.JoinConversationAsync(existing));
        var forUnknown = await Record.ExceptionAsync(() => client.JoinConversationAsync(Guid.NewGuid()));

        Assert.NotNull(forExisting);
        Assert.NotNull(forUnknown);
        Assert.Equal(forExisting.Message, forUnknown.Message);
    }

    [Fact]
    public async Task A_user_who_has_left_cannot_join_again_through_the_hub()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var group = (await aliceApi.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var failure = await Record.ExceptionAsync(() => bobClient.JoinConversationAsync(group));

        Assert.NotNull(failure);
        Assert.Contains(NotAMember, failure.Message);
    }

    [Fact]
    public async Task Joining_the_same_conversation_twice_does_not_duplicate_deliveries()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await using var client = await HubClient.ConnectAsync(_factory, alice);

        await client.JoinConversationAsync(conversation);
        await client.JoinConversationAsync(conversation);
        await PushAsync(_factory, conversation);

        await client.WaitForMessageAsync(conversation);
        await Task.Delay(300);
        Assert.Single(client.Messages);
    }

    [Fact]
    public async Task Reconnecting_joins_the_groups_again()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();

        // Groups are not stored anywhere: a new connection starts with none, so joining on
        // every connect is what restores them after a dropped connection.
        var first = await HubClient.ConnectAsync(_factory, alice);
        await first.StopAsync();
        await first.DisposeAsync();

        await using var second = await HubClient.ConnectAsync(_factory, alice);
        await PushAsync(_factory, conversation);

        await second.WaitForMessageAsync(conversation);
    }

    // Two application instances behind a load balancer: a client is connected to one, and the
    // message is sent through the other. Only the Redis backplane makes that work.
    [Fact]
    public async Task A_push_from_one_server_instance_reaches_a_client_connected_to_another()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();

        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await using var client = await HubClient.ConnectAsync(otherInstance, alice);

        await PushAsync(_factory, conversation);

        await client.WaitForMessageAsync(conversation);
    }

    private static Task PushAsync(WebApplicationFactory<Program> host, Guid conversationId)
    {
        var hub = host.Services.GetRequiredService<IHubContext<ChatHub, IChatClient>>();
        var message = new MessageDto(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "Someone", "hello", DateTime.UtcNow, null);

        return hub.Clients.Group(ChatGroups.Conversation(conversationId)).ReceiveMessage(message);
    }
}
