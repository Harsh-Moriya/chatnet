using System.Net;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Notifications;

// Being added to a group notifies the person. The notification is saved together with the group, so a group
// that was not created tells nobody, and is pushed to the person's open tabs once it is.
[Collection(IntegrationTestCollection.Name)]
public class GroupInvitationNotificationTests
{
    private readonly ChatApiFactory _factory;

    public GroupInvitationNotificationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private Task<List<Notification>> NotificationsOfAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.ConversationId == conversation).ToListAsync());

    [Fact]
    public async Task Everyone_who_is_added_is_notified_and_the_creator_is_not()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);

        var group = (await client.CreateGroupAsync("Book club", bob.Id, carol.Id)).IdOf();

        var notifications = await NotificationsOfAsync(group);
        Assert.Equal(2, notifications.Count);
        Assert.Equal(new[] { bob.Id, carol.Id }.Order(), notifications.Select(n => n.UserId).Order());
        Assert.All(notifications, n =>
        {
            Assert.Equal(NotificationType.MemberAdded, n.Type);
            Assert.Equal(alice.Id, n.ActorId);
            Assert.Equal(group, n.ConversationId);
            Assert.Null(n.MessageId);
            Assert.False(n.IsRead);
        });
    }

    [Fact]
    public async Task A_person_listed_twice_or_the_creator_listed_among_the_members_is_handled_once()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var group = (await client.CreateGroupAsync("Twice", bob.Id, bob.Id, alice.Id)).IdOf();

        var notification = Assert.Single(await NotificationsOfAsync(group));
        Assert.Equal(bob.Id, notification.UserId);
    }

    [Fact]
    public async Task The_added_persons_open_tabs_are_told_and_nobody_else_is()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        await using var bobsOtherTab = await HubClient.ConnectAsync(_factory, bob);
        await using var alicesTab = await HubClient.ConnectAsync(_factory, alice);
        await using var strangersTab = await HubClient.ConnectAsync(_factory, stranger);

        var group = (await client.CreateGroupAsync("Book club", bob.Id)).IdOf();

        foreach (var tab in new[] { bobsTab, bobsOtherTab })
        {
            var pushed = await tab.WaitForNotificationAsync(n => n.GetProperty("conversationId").GetGuid() == group);
            Assert.Equal("MemberAdded", pushed.GetProperty("type").GetString());
            Assert.Equal(alice.Id, pushed.GetProperty("actorId").GetGuid());
            Assert.Equal(alice.DisplayName, pushed.GetProperty("actorDisplayName").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, pushed.GetProperty("messageId").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, pushed.GetProperty("messagePreview").ValueKind);
        }

        Assert.False(await alicesTab.ReceivedNotificationAsync(_ => true));
        Assert.False(await strangersTab.ReceivedNotificationAsync(_ => true));
    }

    [Fact]
    public async Task The_pushed_notification_is_the_one_that_was_saved()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);

        var group = (await client.CreateGroupAsync("Book club", bob.Id)).IdOf();

        var pushed = await bobsTab.WaitForNotificationAsync(n => n.GetProperty("conversationId").GetGuid() == group);
        Assert.Equal(Assert.Single(await NotificationsOfAsync(group)).Id, pushed.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_direct_conversation_notifies_nobody()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);

        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        Assert.Empty(await NotificationsOfAsync(conversation));
        Assert.False(await bobsTab.ReceivedNotificationAsync(_ => true));
    }

    [Fact]
    public async Task A_group_that_is_refused_notifies_nobody()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);

        var response = await client.PostGroupAsync("Ghosts", bob.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await _factory.QueryDbAsync(db => db.Notifications.Where(n => n.UserId == bob.Id).ToListAsync()));
        Assert.False(await bobsTab.ReceivedNotificationAsync(_ => true));
    }
}
