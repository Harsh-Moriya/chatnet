using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Messaging;

// A user who is online when someone starts a conversation with them has no group for it yet. The
// server tells them with an AddedToConversation event; the client answers with JoinConversation.
// The event carries the conversation as that user sees it, so a direct chat is named after the
// OTHER person and the payload differs per recipient.
[Collection(IntegrationTestCollection.Name)]
public class AddedToConversationTests
{
    private readonly ChatApiFactory _factory;

    public AddedToConversationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_other_person_is_told_about_a_new_direct_chat_under_the_name_they_would_see()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        using var aliceApi = _factory.CreateClientFor(alice);

        var created = await aliceApi.CreateDirectAsync(bob.Id);

        var pushed = await bobClient.WaitForAddedAsync(created.IdOf());
        Assert.Equal("Direct", pushed.GetProperty("type").GetString());
        Assert.Equal(alice.DisplayName, pushed.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, pushed.GetProperty("lastMessage").ValueKind);
    }

    [Fact]
    public async Task The_creators_other_tabs_are_told_under_the_other_persons_name()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var aliceOtherTab = await HubClient.ConnectAsync(_factory, alice);
        using var aliceApi = _factory.CreateClientFor(alice);

        var created = await aliceApi.CreateDirectAsync(bob.Id);

        var pushed = await aliceOtherTab.WaitForAddedAsync(created.IdOf());
        Assert.Equal(bob.DisplayName, pushed.GetProperty("name").GetString());
    }

    [Fact]
    public async Task After_the_event_the_client_can_join_and_then_receives_the_conversations_pushes()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        using var aliceApi = _factory.CreateClientFor(alice);
        var id = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await bobClient.WaitForAddedAsync(id);

        await bobClient.JoinConversationAsync(id);
        await PushMessageAsync(id);

        await bobClient.WaitForMessageAsync(id);
    }

    [Fact]
    public async Task Every_online_member_of_a_new_group_is_told_and_outsiders_are_not()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        var outsider = await _factory.RegisterUserAsync("outsider");
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var carolClient = await HubClient.ConnectAsync(_factory, carol);
        await using var outsiderClient = await HubClient.ConnectAsync(_factory, outsider);
        using var aliceApi = _factory.CreateClientFor(alice);

        var id = (await aliceApi.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        var forBob = await bobClient.WaitForAddedAsync(id);
        var forCarol = await carolClient.WaitForAddedAsync(id);
        Assert.Equal("Group", forBob.GetProperty("type").GetString());
        Assert.Equal("Team", forBob.GetProperty("name").GetString());
        Assert.Equal("Team", forCarol.GetProperty("name").GetString());
        Assert.False(await outsiderClient.ReceivedAddedAsync(id));
    }

    [Fact]
    public async Task Asking_for_an_existing_direct_chat_again_does_not_announce_it_again()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        using var aliceApi = _factory.CreateClientFor(alice);
        using var bobApi = _factory.CreateClientFor(bob);

        var id = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await bobClient.WaitForAddedAsync(id);
        await aliceApi.CreateDirectAsync(bob.Id);
        await bobApi.CreateDirectAsync(alice.Id);

        await Task.Delay(500);
        Assert.Single(bobClient.AddedToConversation);
    }

    [Fact]
    public async Task A_request_that_fails_announces_nothing()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        using var aliceApi = _factory.CreateClientFor(alice);

        var response = await aliceApi.PostGroupAsync("Haunted", bob.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await Task.Delay(500);
        Assert.Empty(bobClient.AddedToConversation);
    }

    // The conversation is already saved when the push is attempted, so a failing push must not turn
    // a successful request into an error. The client can still load the conversation with REST.
    [Fact]
    public async Task A_failing_push_does_not_fail_the_request()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var brokenHub = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
        }));
        using var client = brokenHub.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);

        var direct = await client.PostDirectAsync(bob.Id);
        var group = await client.PostGroupAsync("Still works", bob.Id);

        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Equal(HttpStatusCode.Created, group.StatusCode);
        using var normalClient = _factory.CreateClientFor(alice);
        Assert.Equal(2, (await normalClient.ListConversationsAsync()).Count);
    }

    [Fact]
    public async Task A_member_connected_to_another_server_instance_is_told_too()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await using var bobClient = await HubClient.ConnectAsync(otherInstance, bob);
        using var aliceApi = _factory.CreateClientFor(alice);

        var id = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();

        await bobClient.WaitForAddedAsync(id);
    }

    private Task PushMessageAsync(Guid conversationId)
    {
        var hub = _factory.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>();
        var message = new ChatNET.API.Messaging.Models.MessageDto(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "Someone", "hello", DateTime.UtcNow, null, [], null, 0, false);

        return hub.Clients.Group(ChatGroups.Conversation(conversationId)).ReceiveMessage(message);
    }
}
