using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Outbox;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Outbox;

// The pushes of a change to a group, made by the relay when the request's own push failed. Two hosts share the database and
// Redis, as in OutboxRelayTests: the requests go through one whose hub always fails, the relay runs on the other with a clock
// the test moves, and real people are connected to see what reaches them.
[Collection(IntegrationTestCollection.Name)]
public class MembershipPushTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public MembershipPushTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    public Task DisposeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    private WebApplicationFactory<Program> BrokenHub() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
        }));

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    private static Task RunAsync(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

    private async Task<(Guid Group, TestUser Owner, TestUser Member, TestUser Other)> GroupAsync()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var other = await _factory.RegisterUserAsync("other");
        using var client = _factory.CreateClientFor(owner);

        return ((await client.CreateGroupAsync("Team", member.Id, other.Id)).IdOf(), owner, member, other);
    }

    // ##### What the relay delivers #####

    [Fact]
    public async Task A_removal_whose_push_failed_reaches_the_person_removed_and_the_group_through_the_relay()
    {
        var (group, owner, member, other) = await GroupAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, member);
        await using var otherTab = await HubClient.ConnectAsync(_factory, other);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, owner);

        var response = await client.RemoveMemberResponseAsync(group, member.Id);
        Assert.True(response.IsSuccessStatusCode);
        Assert.False(await memberTab.ReceivedRemovedAsync(group));
        Assert.False(await otherTab.ReceivedMembersChangedAsync(group));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var leftAt = (await _factory.QueryDbAsync(db => db.ConversationMembers
            .SingleAsync(m => m.ConversationId == group && m.UserId == member.Id))).LeftAt!.Value;
        Assert.Equal(leftAt, (await memberTab.WaitForRemovedAsync(group)).LeftAt);
        var change = await otherTab.WaitForMembersChangedAsync(group);
        Assert.Equal([member.Id], change.GetProperty("removedUserIds").EnumerateArray().Select(id => id.GetGuid()));
        Assert.False(await memberTab.ReceivedMembersChangedAsync(group));
        var rows = await _factory.OutboxRowsAboutAsync("conversationId", group);
        Assert.All(rows.Where(r => r.Kind is "MembersChanged" or "MemberLeft"), r => Assert.NotNull(r.DispatchedAt));
    }

    [Fact]
    public async Task An_add_whose_push_failed_reaches_the_new_person_and_the_group_through_the_relay()
    {
        var (group, owner, _, other) = await GroupAsync();
        var newcomer = await _factory.RegisterUserAsync("newcomer");
        await using var newcomerTab = await HubClient.ConnectAsync(_factory, newcomer);
        await using var otherTab = await HubClient.ConnectAsync(_factory, other);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, owner);

        await client.AddMembersResponseAsync(group, newcomer.Id);
        Assert.False(await newcomerTab.ReceivedAddedAsync(group));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var added = await newcomerTab.WaitForAddedAsync(group);
        Assert.Equal("Team", added.GetProperty("name").GetString());
        var change = await otherTab.WaitForMembersChangedAsync(group);
        Assert.Equal([newcomer.Id], change.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()));
    }

    [Fact]
    public async Task A_rename_whose_push_failed_reaches_the_group_through_the_relay()
    {
        var (group, owner, member, _) = await GroupAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, member);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, owner);

        await client.RenameResponseAsync(group, "Crew");
        Assert.False(await memberTab.ReceivedConversationUpdatedAsync(group));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        Assert.Equal("Crew", (await memberTab.WaitForConversationUpdatedAsync(group)).GetProperty("name").GetString());
    }

    // ##### Late pushes say what is true when they are made #####

    [Fact]
    public async Task A_late_removal_push_says_nothing_about_a_person_who_has_been_added_back()
    {
        var (group, owner, member, _) = await GroupAsync();
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var brokenOwner = Authorized(broken, owner);
        using var ownerClient = _factory.CreateClientFor(owner);

        await brokenOwner.RemoveMemberResponseAsync(group, member.Id);
        await ownerClient.AddMembersAsync(group, member.Id);
        await using var memberTab = await HubClient.ConnectAsync(_factory, member);

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        Assert.False(await memberTab.ReceivedRemovedAsync(group));
        var left = Assert.Single(await _factory.OutboxRowsAboutAsync("conversationId", group), r => r.Kind == "MemberLeft");
        Assert.NotNull(left.DispatchedAt);
    }

    [Fact]
    public async Task A_late_push_of_the_owner_leaving_names_the_new_owner_as_well_as_the_one_who_left()
    {
        var (group, owner, member, other) = await GroupAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, member);
        await using var otherTab = await HubClient.ConnectAsync(_factory, other);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var brokenOwner = Authorized(broken, owner);

        await brokenOwner.LeaveResponseAsync(group);
        var newOwner = (await _factory.QueryDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.LeftAt == null && m.Role == ChatNET.API.Conversations.Models.MemberRole.Owner)
            .ToListAsync())).Single().UserId;
        Assert.False(await memberTab.ReceivedMembersChangedAsync(group));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        foreach (var tab in new[] { memberTab, otherTab })
        {
            var change = await tab.WaitForMembersChangedAsync(group);
            var entry = Assert.Single(change.GetProperty("members").EnumerateArray());
            Assert.Equal(newOwner, entry.GetProperty("userId").GetGuid());
            Assert.Equal("Owner", entry.GetProperty("role").GetString());
            Assert.Equal([owner.Id], change.GetProperty("removedUserIds").EnumerateArray().Select(id => id.GetGuid()));
        }
    }

    [Fact]
    public async Task A_late_add_push_says_nothing_to_a_person_who_has_left_again()
    {
        var (group, owner, _, _) = await GroupAsync();
        var newcomer = await _factory.RegisterUserAsync("newcomer");
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var brokenOwner = Authorized(broken, owner);
        using var newcomerClient = _factory.CreateClientFor(newcomer);

        await brokenOwner.AddMembersResponseAsync(group, newcomer.Id);
        await newcomerClient.LeaveResponseAsync(group);
        await using var newcomerTab = await HubClient.ConnectAsync(_factory, newcomer);

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        Assert.False(await newcomerTab.ReceivedAddedAsync(group));
        var joined = Assert.Single(await _factory.OutboxRowsAboutAsync("conversationId", group), r => r.Kind == "MembersJoined");
        Assert.NotNull(joined.DispatchedAt);
    }

    [Fact]
    public async Task A_late_push_of_a_change_to_the_people_carries_their_entries_as_they_are_then()
    {
        var (group, owner, member, other) = await GroupAsync();
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var brokenOwner = Authorized(broken, owner);
        using var ownerClient = _factory.CreateClientFor(owner);
        await using var otherTab = await HubClient.ConnectAsync(_factory, other);

        await brokenOwner.ChangeRoleResponseAsync(group, member.Id, "Admin");
        await ownerClient.ChangeRoleResponseAsync(group, member.Id, "Member");

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        // Two pushes reach the tab: the one the second request made at once, and the late one for the first request,
        // which was built when the relay made it. Both say what the entry is now.
        for (var i = 0; i < 100 && otherTab.MembersChanges.Count < 2; i++) await Task.Delay(50);

        Assert.Equal(2, otherTab.MembersChanges.Count);
        foreach (var change in otherTab.MembersChanges)
        {
            var entry = Assert.Single(change.GetProperty("members").EnumerateArray());
            Assert.Equal(member.Id, entry.GetProperty("userId").GetGuid());
            Assert.Equal("Member", entry.GetProperty("role").GetString());
        }
    }
}
