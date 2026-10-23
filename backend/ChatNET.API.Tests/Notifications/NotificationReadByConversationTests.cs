using ChatNET.API.Notifications.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Notifications;

// Reading a conversation up to a message is also reading the mentions in it up to that message, so the bell does
// not keep asking about what is already on the screen.
[Collection(IntegrationTestCollection.Name)]
public class NotificationReadByConversationTests
{
    private readonly ChatApiFactory _factory;

    public NotificationReadByConversationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(TestUser Me, TestUser Other, Guid Group)> SetUpAsync()
    {
        var me = await _factory.RegisterUserAsync("me");
        var other = await _factory.RegisterUserAsync("other");
        using var client = _factory.CreateClientFor(other);
        var group = (await client.CreateGroupAsync("Reading", me.Id)).IdOf();

        return (me, other, group);
    }

    private Task<List<Notification>> MentionsOfAsync(Guid user, Guid conversation) =>
        _factory.QueryDbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.UserId == user && n.ConversationId == conversation && n.Type == NotificationType.Mention)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync());

    [Fact]
    public async Task Reading_up_to_a_message_marks_the_mentions_up_to_it_and_leaves_later_ones()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        var first = await otherClient.SendMessageAsync(group, $"@{me.UserName} one");
        var second = await otherClient.SendMessageAsync(group, "plain");
        await otherClient.SendMessageAsync(group, $"@{me.UserName} three");

        await client.MarkReadAsync(group, second.MessageIdOf());

        var mentions = await MentionsOfAsync(me.Id, group);
        Assert.Equal([true, false], mentions.Select(n => n.IsRead));
        Assert.Equal(first.MessageIdOf(), mentions[0].MessageId);
    }

    [Fact]
    public async Task Reading_further_marks_the_rest()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        await otherClient.SendMessageAsync(group, $"@{me.UserName} one");
        var last = await otherClient.SendMessageAsync(group, $"@{me.UserName} two");

        await client.MarkReadAsync(group, last.MessageIdOf());

        Assert.All(await MentionsOfAsync(me.Id, group), n => Assert.True(n.IsRead));
    }

    [Fact]
    public async Task A_mention_in_another_conversation_is_not_touched()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        var elsewhere = (await otherClient.CreateGroupAsync("Elsewhere", me.Id)).IdOf();
        await otherClient.SendMessageAsync(elsewhere, $"@{me.UserName} over here");
        var here = await otherClient.SendMessageAsync(group, $"@{me.UserName} over there");

        await client.MarkReadAsync(group, here.MessageIdOf());

        Assert.False(Assert.Single(await MentionsOfAsync(me.Id, elsewhere)).IsRead);
        Assert.True(Assert.Single(await MentionsOfAsync(me.Id, group)).IsRead);
    }

    [Fact]
    public async Task Being_added_to_the_group_is_not_a_mention_and_stays_unread()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        var message = await otherClient.SendMessageAsync(group, "hello everyone");

        await client.MarkReadAsync(group, message.MessageIdOf());

        var invitation = Assert.Single(await _factory.NotificationRowsAsync(me.Id), n => n.Type == NotificationType.MemberAdded);
        Assert.False(invitation.IsRead);
    }

    // Only mentions are swept. A notification of another kind that points at a message (none is written today) is not
    // "a mention that was read" and stays as it is.
    [Fact]
    public async Task A_notification_of_another_kind_that_points_at_a_message_is_not_swept()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        var message = await otherClient.SendMessageAsync(group, "plain");
        var invitation = (await _factory.AddNotificationsAsync(
            me.Id, other.Id, group, 1, DateTime.UtcNow.AddDays(-1), type: NotificationType.MemberAdded, messageId: message.MessageIdOf()))[0];

        await client.MarkReadAsync(group, message.MessageIdOf());

        Assert.False(Assert.Single(await _factory.NotificationRowsAsync(me.Id), n => n.Id == invitation).IsRead);
    }

    [Fact]
    public async Task Someone_elses_mentions_are_not_touched_by_my_reading()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        await otherClient.SendMessageAsync(group, $"@{me.UserName} hi");
        await client.SendMessageAsync(group, $"@{other.UserName} hello back");
        var last = await otherClient.SendMessageAsync(group, "anyone there");

        await client.MarkReadAsync(group, last.MessageIdOf());

        Assert.False(Assert.Single(await MentionsOfAsync(other.Id, group)).IsRead);
    }

    [Fact]
    public async Task All_of_my_open_tabs_are_told_which_mentions_were_marked_and_the_other_person_is_not()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        await otherClient.SendMessageAsync(group, $"@{me.UserName} one");
        var last = await otherClient.SendMessageAsync(group, $"@{me.UserName} two");
        var expected = (await MentionsOfAsync(me.Id, group)).Select(n => n.Id).ToArray();
        await using var thisTab = await HubClient.ConnectAsync(_factory, me);
        await using var otherTab = await HubClient.ConnectAsync(_factory, me);
        await using var theirTab = await HubClient.ConnectAsync(_factory, other);

        await client.MarkReadAsync(group, last.MessageIdOf());

        foreach (var tab in new[] { thisTab, otherTab })
            Assert.Equal(expected.Order(), (await tab.WaitForNotificationsReadAsync(_ => true)).Order());

        Assert.False(await theirTab.ReceivedNotificationsReadAsync());
    }

    [Fact]
    public async Task Reading_a_conversation_with_no_mentions_tells_nobody_anything_about_notifications()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        var message = await otherClient.SendMessageAsync(group, "no names here");
        await using var tab = await HubClient.ConnectAsync(_factory, me);

        await client.MarkReadAsync(group, message.MessageIdOf());

        Assert.False(await tab.ReceivedNotificationsReadAsync());
    }

    // A read that does not move the position (the same message again) has nothing new to say.
    [Fact]
    public async Task Reading_the_same_message_again_does_not_tell_anyone_again()
    {
        var (me, other, group) = await SetUpAsync();
        using var otherClient = _factory.CreateClientFor(other);
        using var client = _factory.CreateClientFor(me);
        var message = await otherClient.SendMessageAsync(group, $"@{me.UserName} one");
        await client.MarkReadAsync(group, message.MessageIdOf());
        await using var tab = await HubClient.ConnectAsync(_factory, me);

        await client.MarkReadAsync(group, message.MessageIdOf());

        Assert.False(await tab.ReceivedNotificationsReadAsync());
    }
}
