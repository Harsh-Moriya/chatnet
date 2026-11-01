using System.Net;
using System.Net.Http.Headers;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

[Collection(IntegrationTestCollection.Name)]
public class HubConnectionTests
{
    private readonly ChatApiFactory _factory;

    public HubConnectionTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_valid_token_connects()
    {
        var alice = await _factory.RegisterUserAsync("alice");

        await using var client = await HubClient.ConnectAsync(_factory, alice);

        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task Connecting_without_a_token_is_refused_with_401()
    {
        var failure = await Record.ExceptionAsync(() => HubClient.ConnectAsync(_factory, (string?)null));

        Assert.NotNull(failure);
        Assert.Contains("401", failure.Message);
    }

    [Fact]
    public async Task Connecting_with_a_garbage_token_is_refused_with_401()
    {
        var failure = await Record.ExceptionAsync(() => HubClient.ConnectAsync(_factory, "not.a.jwt"));

        Assert.NotNull(failure);
        Assert.Contains("401", failure.Message);
    }

    // The query string token exists only because browsers cannot set headers on a WebSocket
    // upgrade. Anywhere else it would put tokens into URLs, so it must be ignored.
    [Fact]
    public async Task A_token_in_the_query_string_is_ignored_outside_the_hub()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/conversations?access_token={alice.Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Also settles that SignalR resolves the user ID from the token's "sub" claim: if it did not,
    // Clients.User(...) would reach nobody.
    [Fact]
    public async Task A_push_addressed_to_a_user_reaches_every_tab_of_that_user_and_nobody_else()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var aliceTab1 = await HubClient.ConnectAsync(_factory, alice);
        await using var aliceTab2 = await HubClient.ConnectAsync(_factory, alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var conversation = SampleConversation();

        await HubContext().Clients.User(alice.Id.ToString()).AddedToConversation(conversation);

        await aliceTab1.WaitForAddedAsync(conversation.Id);
        await aliceTab2.WaitForAddedAsync(conversation.Id);
        Assert.False(await bobTab.ReceivedAddedAsync(conversation.Id));
    }

    [Fact]
    public async Task Enums_in_hub_payloads_are_sent_as_strings()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        await using var client = await HubClient.ConnectAsync(_factory, alice);
        var conversation = SampleConversation();

        await HubContext().Clients.User(alice.Id.ToString()).AddedToConversation(conversation);

        var received = await client.WaitForAddedAsync(conversation.Id);
        Assert.Equal("Group", received.GetProperty("type").GetString());
    }

    private IHubContext<ChatHub, IChatClient> HubContext() =>
        _factory.Services.GetRequiredService<IHubContext<ChatHub, IChatClient>>();

    private static ConversationDto SampleConversation() =>
        new(Guid.NewGuid(), ConversationType.Group, "Team", null, DateTime.UtcNow, null, 0, null, false, null);
}
