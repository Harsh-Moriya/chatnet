using System.Collections.Concurrent;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Presence;
using ChatNET.API.Tests.Conversations;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Messaging;

// A person who stops being a member of a group must stop hearing it at once, in every tab, on whichever server instance the
// tab is connected to, and must not be able to type into it. Joining a group is a thing a connection does once, when it
// opens, so without this a removed person went on receiving the group until their next reconnect.
[Collection(IntegrationTestCollection.Name)]
public class ConnectionEvictionTests
{
    private readonly ChatApiFactory _factory;

    public ConnectionEvictionTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // An owner and two members in a group, and another group the two members share with a third person, to show that
    // only the conversation the person left is affected.
    private sealed record Team(Guid Group, Guid Other, TestUser Owner, TestUser Member, TestUser Friend);

    private async Task<Team> TeamAsync()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var friend = await _factory.RegisterUserAsync("friend");
        using var ownerClient = _factory.CreateClientFor(owner);
        using var friendClient = _factory.CreateClientFor(friend);

        var group = (await ownerClient.CreateGroupAsync("Team", member.Id, friend.Id)).IdOf();
        var other = (await friendClient.CreateGroupAsync("Elsewhere", member.Id)).IdOf();

        return new Team(group, other, owner, member, friend);
    }

    // The message reaches an instance a moment after it was published, so a test waits for the instance to have acted on it.
    private static async Task WaitForEvictionAsync(WebApplicationFactory<Program> host, long before)
    {
        var evictor = host.Services.GetRequiredService<ConnectionEvictor>();

        for (var i = 0; i < 200 && evictor.EvictionsHandled <= before; i++)
            await Task.Delay(25);

        Assert.True(evictor.EvictionsHandled > before, "The instance did not act on the eviction message.");
    }

    private static Task ListeningAsync(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<ConnectionEvictionListener>().Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

    private static long HandledBy(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<ConnectionEvictor>().EvictionsHandled;

    // ##### Removing and leaving #####

    [Fact]
    public async Task A_removed_member_stops_hearing_the_group_in_every_tab_but_keeps_their_other_conversations()
    {
        var team = await TeamAsync();
        await ListeningAsync(_factory);
        await using var firstTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var secondTab = await HubClient.ConnectAsync(_factory, team.Member);
        using var owner = _factory.CreateClientFor(team.Owner);
        using var friend = _factory.CreateClientFor(team.Friend);
        var before = HandledBy(_factory);

        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        await WaitForEvictionAsync(_factory, before);
        await owner.SendMessageAsync(team.Group, "said after the removal");
        await friend.SendMessageAsync(team.Other, "said in the other group");

        foreach (var tab in new[] { firstTab, secondTab })
        {
            await tab.WaitForMessageAsync(team.Other);
            Assert.False(await tab.ReceivedMessageAsync(team.Group));
        }
    }

    [Fact]
    public async Task A_removed_member_cannot_make_the_group_hear_them_type()
    {
        var team = await TeamAsync();
        await ListeningAsync(_factory);
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var friendTab = await HubClient.ConnectAsync(_factory, team.Friend);
        await memberTab.StartTypingAsync(team.Group);
        await friendTab.WaitForTypingAsync(t => t.UserId == team.Member.Id && t.IsTyping);
        using var owner = _factory.CreateClientFor(team.Owner);
        var before = HandledBy(_factory);

        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        await WaitForEvictionAsync(_factory, before);

        await Assert.ThrowsAsync<HubException>(() => memberTab.StartTypingAsync(team.Group));
    }

    [Fact]
    public async Task Someone_who_leaves_stops_hearing_the_group_too()
    {
        var team = await TeamAsync();
        await ListeningAsync(_factory);
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        using var member = _factory.CreateClientFor(team.Member);
        using var owner = _factory.CreateClientFor(team.Owner);
        var before = HandledBy(_factory);

        await member.LeaveResponseAsync(team.Group);
        await WaitForEvictionAsync(_factory, before);
        await owner.SendMessageAsync(team.Group, "said after they left");

        Assert.False(await memberTab.ReceivedMessageAsync(team.Group));
    }

    [Fact]
    public async Task The_people_who_stay_go_on_hearing_the_group()
    {
        var team = await TeamAsync();
        await ListeningAsync(_factory);
        await using var friendTab = await HubClient.ConnectAsync(_factory, team.Friend);
        using var owner = _factory.CreateClientFor(team.Owner);
        var before = HandledBy(_factory);

        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        await WaitForEvictionAsync(_factory, before);
        var sent = await owner.SendMessageAsync(team.Group, "for the ones who are still here");

        await friendTab.WaitForMessageAsync(team.Group, sent.MessageIdOf());
    }

    // ##### Another server instance #####

    [Fact]
    public async Task A_connection_on_another_server_instance_is_taken_out_too()
    {
        var team = await TeamAsync();
        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await ListeningAsync(_factory);
        await ListeningAsync(otherInstance);
        await using var memberTab = await HubClient.ConnectAsync(otherInstance, team.Member);
        using var owner = _factory.CreateClientFor(team.Owner);
        var before = HandledBy(otherInstance);

        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        await WaitForEvictionAsync(otherInstance, before);
        await owner.SendMessageAsync(team.Group, "said after the removal");

        Assert.False(await memberTab.ReceivedMessageAsync(team.Group));
    }

    // ##### Coming back #####

    [Fact]
    public async Task Someone_added_back_hears_the_group_and_can_type_in_it_again_once_their_tab_joins_it()
    {
        var team = await TeamAsync();
        await ListeningAsync(_factory);
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var friendTab = await HubClient.ConnectAsync(_factory, team.Friend);
        using var owner = _factory.CreateClientFor(team.Owner);
        await memberTab.StartTypingAsync(team.Group);
        await friendTab.WaitForTypingAsync(t => t.UserId == team.Member.Id && t.IsTyping);
        var before = HandledBy(_factory);
        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        await WaitForEvictionAsync(_factory, before);

        await owner.AddMembersAsync(team.Group, team.Member.Id);
        await memberTab.WaitForAddedAsync(team.Group);
        await memberTab.JoinConversationAsync(team.Group);
        var sent = await owner.SendMessageAsync(team.Group, "welcome back");
        await memberTab.StartTypingAsync(team.Group);

        await memberTab.WaitForMessageAsync(team.Group, sent.MessageIdOf());
        // Typing again straight away is delivered: what was held back about the earlier typing went with the removal.
        for (var i = 0; i < 100 && friendTab.TypingEvents.Count(t => t.UserId == team.Member.Id && t.IsTyping) < 2; i++)
            await Task.Delay(25);
        Assert.Equal(2, friendTab.TypingEvents.Count(t => t.UserId == team.Member.Id && t.IsTyping));
    }

    // ##### Races of joining #####

    private WebApplicationFactory<Program> HostWithStaleMembership(
        ConcurrentQueue<bool> activeChecks, ConcurrentQueue<IReadOnlyList<Guid>> activeLists) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConversationRepository>();
            services.AddScoped<IConversationRepository>(provider => new StaleConversationRepository(
                ActivatorUtilities.CreateInstance<ConversationRepository>(provider), activeChecks, activeLists));
        }));

    // The check that lets a person join passed, and then they were removed, before the join was made. The join is checked
    // again afterwards, so the connection does not stay in a group its person is not in.
    [Fact]
    public async Task A_join_that_a_removal_slipped_in_front_of_leaves_the_connection_out_of_the_group()
    {
        var team = await TeamAsync();
        using var owner = _factory.CreateClientFor(team.Owner);
        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        var checks = new ConcurrentQueue<bool>([true]);
        using var host = HostWithStaleMembership(checks, new ConcurrentQueue<IReadOnlyList<Guid>>());
        await using var memberTab = await HubClient.ConnectAsync(host, team.Member);

        await Assert.ThrowsAsync<HubException>(() => memberTab.JoinConversationAsync(team.Group));
        await owner.SendMessageAsync(team.Group, "said after the removal");

        Assert.Empty(checks);
        Assert.False(await memberTab.ReceivedMessageAsync(team.Group));
        await Assert.ThrowsAsync<HubException>(() => memberTab.StartTypingAsync(team.Group));
    }

    // The list of conversations a connection is put into was read, and then the person was removed from one of them, before
    // the joins were made. The list is read again after the joins.
    [Fact]
    public async Task A_connection_that_opened_while_a_removal_was_made_is_not_left_in_the_group()
    {
        var team = await TeamAsync();
        using var owner = _factory.CreateClientFor(team.Owner);
        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        var lists = new ConcurrentQueue<IReadOnlyList<Guid>>([[team.Group, team.Other]]);
        using var host = HostWithStaleMembership(new ConcurrentQueue<bool>(), lists);
        await using var memberTab = await HubClient.ConnectAsync(host, team.Member);
        using var friend = _factory.CreateClientFor(team.Friend);

        await owner.SendMessageAsync(team.Group, "said after the removal");
        await friend.SendMessageAsync(team.Other, "said in the other group");

        Assert.Empty(lists);
        await memberTab.WaitForMessageAsync(team.Other);
        Assert.False(await memberTab.ReceivedMessageAsync(team.Group));
        await Assert.ThrowsAsync<HubException>(() => memberTab.StartTypingAsync(team.Group));
    }

    // ##### The registry and the message #####

    [Fact]
    public async Task A_connection_is_forgotten_when_it_closes_and_a_person_with_none_is_not_kept()
    {
        var team = await TeamAsync();
        using var host = _factory.WithWebHostBuilder(_ => { });
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        var tab = await HubClient.ConnectAsync(host, team.Member);
        var secondTab = await HubClient.ConnectAsync(host, team.Member);
        Assert.Equal(2, registry.Of(team.Member.Id).Count);
        Assert.Equal(1, registry.UserCount);

        await tab.StopAsync();
        await tab.DisposeAsync();
        for (var i = 0; i < 200 && registry.Of(team.Member.Id).Count > 1; i++) await Task.Delay(25);
        Assert.Single(registry.Of(team.Member.Id));
        Assert.Equal(1, registry.UserCount);

        await secondTab.StopAsync();
        await secondTab.DisposeAsync();
        for (var i = 0; i < 200 && registry.UserCount > 0; i++) await Task.Delay(25);
        Assert.Empty(registry.Of(team.Member.Id));
        Assert.Equal(0, registry.UserCount);
    }

    [Fact]
    public async Task A_message_that_is_not_a_person_and_a_conversation_is_ignored_and_the_next_real_one_still_works()
    {
        var team = await TeamAsync();
        await ListeningAsync(_factory);
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        var redis = _factory.Services.GetRequiredService<RedisConnection>();
        var subscriber = await redis.GetSubscriberAsync();
        await subscriber.PublishAsync(ConnectionEvictor.Channel, "nonsense");
        using var owner = _factory.CreateClientFor(team.Owner);
        var before = HandledBy(_factory);

        await owner.RemoveMemberResponseAsync(team.Group, team.Member.Id);
        await WaitForEvictionAsync(_factory, before);
        await owner.SendMessageAsync(team.Group, "said after the removal");

        Assert.False(await memberTab.ReceivedMessageAsync(team.Group));
    }

    [Fact]
    public void The_message_reads_back_as_the_person_and_the_conversation_it_was_made_from()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var read = ConnectionEvictor.TryDecode(ConnectionEvictor.Encode(userId, conversationId), out var user, out var conversation);

        Assert.True(read);
        Assert.Equal(userId, user);
        Assert.Equal(conversationId, conversation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("not-a-guid:also-not")]
    [InlineData("00000000-0000-0000-0000-000000000001:00000000-0000-0000-0000-000000000002:extra")]
    public void A_message_that_is_not_two_ids_is_not_read(string? message)
    {
        Assert.False(ConnectionEvictor.TryDecode(message, out _, out _));
    }
}
