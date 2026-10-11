using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// Typing indicators are ephemeral: they are never stored and cost no database work. Because they
// fan out to a whole conversation, the hub must make sure the sender really is in the
// conversation, and must stop one client from turning a keystroke into a flood.
[Collection(IntegrationTestCollection.Name)]
public class HubTypingTests
{
    private const string NotAMember = "You are not a member of this conversation.";

    private readonly ChatApiFactory _factory;

    public HubTypingTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Other_members_are_told_who_is_typing_in_which_conversation()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await aliceClient.StartTypingAsync(conversation);

        // The name comes from the sender's token: a client cannot choose what others see.
        var typing = await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation);
        Assert.Equal(alice.Id, typing.UserId);
        Assert.Equal(alice.DisplayName, typing.DisplayName);
        Assert.True(typing.IsTyping);
    }

    [Fact]
    public async Task Stopping_tells_the_others_that_typing_ended()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await aliceClient.StartTypingAsync(conversation);
        await aliceClient.StopTypingAsync(conversation);

        var stopped = await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation && !t.IsTyping);
        Assert.Equal(alice.Id, stopped.UserId);
    }

    [Fact]
    public async Task The_connection_that_is_typing_does_not_receive_its_own_event()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await aliceClient.StartTypingAsync(conversation);
        await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation);

        Assert.False(await aliceClient.ReceivedTypingAsync(t => t.ConversationId == conversation));
    }

    [Fact]
    public async Task Members_of_other_conversations_are_not_told()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var carolClient = await HubClient.ConnectAsync(_factory, carol);

        await aliceClient.StartTypingAsync(conversation);

        await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation);
        Assert.False(await carolClient.ReceivedTypingAsync(_ => true));
    }

    // Without this check anyone who knows (or guesses) a conversation ID could push "X is typing"
    // into a private conversation.
    [Fact]
    public async Task Someone_outside_the_conversation_cannot_send_typing_events_into_it()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var intruderClient = await HubClient.ConnectAsync(_factory, intruder);

        var failure = await Record.ExceptionAsync(() => intruderClient.StartTypingAsync(conversation));

        Assert.NotNull(failure);
        Assert.Contains(NotAMember, failure.Message);
        Assert.False(await bobClient.ReceivedTypingAsync(_ => true));
        Assert.False(await aliceClient.ReceivedTypingAsync(_ => true));
    }

    [Fact]
    public async Task A_member_must_have_joined_on_this_connection_before_typing_in_a_new_conversation()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await bobClient.JoinConversationAsync(conversation);

        var beforeJoining = await Record.ExceptionAsync(() => aliceClient.StartTypingAsync(conversation));
        await aliceClient.JoinConversationAsync(conversation);
        await aliceClient.StartTypingAsync(conversation);

        Assert.NotNull(beforeJoining);
        await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation && t.IsTyping);
    }

    [Fact]
    public async Task Stopping_typing_in_a_conversation_the_connection_is_not_in_does_nothing()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var intruderClient = await HubClient.ConnectAsync(_factory, intruder);

        var failure = await Record.ExceptionAsync(() => intruderClient.StopTypingAsync(conversation));

        Assert.Null(failure);
        Assert.False(await bobClient.ReceivedTypingAsync(_ => true));
    }

    // A client sends "typing" on keystrokes. The server keeps only one per second per conversation,
    // so a misbehaving client cannot multiply itself across every member of a large group.
    [Fact]
    public async Task Repeated_typing_within_a_second_is_delivered_once()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        for (var i = 0; i < 10; i++)
            await aliceClient.StartTypingAsync(conversation);

        await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation);
        await Task.Delay(300);
        Assert.Single(bobClient.TypingEvents);
    }

    [Fact]
    public async Task Typing_is_delivered_again_once_the_interval_has_passed()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await aliceClient.StartTypingAsync(conversation);
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await aliceClient.StartTypingAsync(conversation);

        await WaitForCountAsync(bobClient, 2);
    }

    // Stopping is never throttled, and it resets the clock: someone who stops and starts again
    // has changed state twice, and the others should see both changes at once.
    [Fact]
    public async Task Stop_is_never_throttled_and_typing_again_after_it_is_delivered_immediately()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await aliceClient.StartTypingAsync(conversation);
        await aliceClient.StopTypingAsync(conversation);
        await aliceClient.StartTypingAsync(conversation);

        await WaitForCountAsync(bobClient, 3);
        Assert.Equal(new[] { true, false, true }, bobClient.TypingEvents.Select(t => t.IsTyping));
    }

    [Fact]
    public async Task Typing_costs_no_database_queries()
    {
        var counter = new QueryCounter();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(counter))));
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        await using var aliceClient = await HubClient.ConnectAsync(host, alice);
        await using var bobClient = await HubClient.ConnectAsync(host, bob);

        counter.Reset();
        await aliceClient.StartTypingAsync(conversation);
        await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation);
        await aliceClient.StopTypingAsync(conversation);
        await bobClient.WaitForTypingAsync(t => !t.IsTyping);

        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Typing_reaches_a_member_connected_to_another_server_instance()
    {
        var (alice, bob, conversation) = await TwoUsersWithADirectChatAsync();
        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(otherInstance, bob);

        await aliceClient.StartTypingAsync(conversation);

        await bobClient.WaitForTypingAsync(t => t.ConversationId == conversation && t.UserId == alice.Id);
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid Conversation)> TwoUsersWithADirectChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        return (alice, bob, conversation);
    }

    private static async Task WaitForCountAsync(HubClient client, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.TypingEvents.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(client.TypingEvents.Count >= count,
            $"Expected {count} typing events but received {client.TypingEvents.Count}.");
    }
}
