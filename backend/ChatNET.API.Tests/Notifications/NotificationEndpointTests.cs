using System.Net;
using System.Text.Json;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Notifications;

// The bell: a person's own notifications, newest first, read and unread together; marking some or all of them
// read; dismissing one. Everything is scoped to the caller, so someone else's notification is never visible,
// changed or even confirmed to exist.
[Collection(IntegrationTestCollection.Name)]
public class NotificationEndpointTests
{
    private readonly ChatApiFactory _factory;

    public NotificationEndpointTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-30);

    // A person, a group they share with another, and a conversation to point the notifications at.
    private async Task<(TestUser Me, TestUser Other, Guid Group)> SetUpAsync()
    {
        var me = await _factory.RegisterUserAsync("me");
        var other = await _factory.RegisterUserAsync("other");
        using var client = _factory.CreateClientFor(other);

        return (me, other, (await client.CreateGroupAsync("Bell", me.Id)).IdOf());
    }

    // Creating the group notified "me" already; start every test from a clean bell.
    private async Task<(TestUser Me, TestUser Other, Guid Group)> SetUpEmptyAsync()
    {
        var setUp = await SetUpAsync();
        await _factory.ExecuteDbAsync(db => db.Notifications.Where(n => n.UserId == setUp.Me.Id).ExecuteDeleteAsync());
        return setUp;
    }

    // ##### The list #####

    [Fact]
    public async Task A_person_with_no_notifications_gets_an_empty_list_and_a_zero_count()
    {
        var (me, _, _) = await SetUpEmptyAsync();
        using var client = _factory.CreateClientFor(me);

        var list = await client.GetNotificationsAsync();

        Assert.Empty(list.ItemsOf());
        Assert.Equal(0, list.UnreadCountOf());
    }

    [Fact]
    public async Task Notifications_come_newest_first_whether_they_are_read_or_not()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 6, LongAgo, isRead: i => i % 2 == 0);
        using var client = _factory.CreateClientFor(me);

        var list = await client.GetNotificationsAsync();

        Assert.Equal(Enumerable.Reverse(ids), list.IdsOf());
        Assert.Equal([false, true, false, true, false, true], list.ItemsOf().Select(n => n.GetProperty("isRead").GetBoolean()));
        Assert.Equal(3, list.UnreadCountOf());
    }

    [Fact]
    public async Task A_mention_arrives_with_the_name_of_who_mentioned_you_and_a_preview_of_the_message()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        using var otherClient = _factory.CreateClientFor(other);
        var message = await otherClient.SendMessageAsync(group, $"@{me.UserName} can you look at this?");
        using var client = _factory.CreateClientFor(me);

        var item = Assert.Single((await client.GetNotificationsAsync()).ItemsOf());

        Assert.Equal("Mention", item.GetProperty("type").GetString());
        Assert.Equal(group, item.GetProperty("conversationId").GetGuid());
        Assert.Equal(other.Id, item.GetProperty("actorId").GetGuid());
        Assert.Equal(other.DisplayName, item.GetProperty("actorDisplayName").GetString());
        Assert.Equal(message.MessageIdOf(), item.GetProperty("messageId").GetGuid());
        Assert.Equal($"@{me.UserName} can you look at this?", item.GetProperty("messagePreview").GetString());
        Assert.False(item.GetProperty("isRead").GetBoolean());
        Assert.True(item.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Being_added_to_a_group_arrives_as_a_notification_without_a_message()
    {
        var other = await _factory.RegisterUserAsync("other");
        var me = await _factory.RegisterUserAsync("me");
        using var otherClient = _factory.CreateClientFor(other);
        var group = (await otherClient.CreateGroupAsync("Book club", me.Id)).IdOf();
        using var client = _factory.CreateClientFor(me);

        var item = Assert.Single((await client.GetNotificationsAsync()).ItemsOf());

        Assert.Equal("MemberAdded", item.GetProperty("type").GetString());
        Assert.Equal(group, item.GetProperty("conversationId").GetGuid());
        Assert.Equal(other.DisplayName, item.GetProperty("actorDisplayName").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("messageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("messagePreview").ValueKind);
    }

    [Fact]
    public async Task The_preview_is_cut_at_one_hundred_characters()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        using var otherClient = _factory.CreateClientFor(other);
        var text = $"@{me.UserName} " + new string('y', 200);
        await otherClient.SendMessageAsync(group, text);
        using var client = _factory.CreateClientFor(me);

        var item = Assert.Single((await client.GetNotificationsAsync()).ItemsOf());

        Assert.Equal(text[..100], item.GetProperty("messagePreview").GetString());
    }

    [Fact]
    public async Task A_deleted_message_leaves_the_notification_with_no_preview()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        using var otherClient = _factory.CreateClientFor(other);
        var message = await otherClient.SendMessageAsync(group, $"@{me.UserName} secret");
        await _factory.ExecuteDbAsync(db => db.Messages
            .Where(m => m.Id == message.MessageIdOf())
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true)));
        using var client = _factory.CreateClientFor(me);

        var item = Assert.Single((await client.GetNotificationsAsync()).ItemsOf());

        Assert.Equal(JsonValueKind.Null, item.GetProperty("messagePreview").ValueKind);
        Assert.Equal(message.MessageIdOf(), item.GetProperty("messageId").GetGuid());
    }

    [Fact]
    public async Task Only_the_callers_own_notifications_are_listed_and_counted()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        await _factory.AddNotificationsAsync(me.Id, other.Id, group, 2, LongAgo);
        await _factory.AddNotificationsAsync(other.Id, me.Id, group, 5, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var list = await client.GetNotificationsAsync();

        Assert.Equal(2, list.ItemsOf().Count);
        Assert.Equal(2, list.UnreadCountOf());
    }

    [Fact]
    public async Task The_unread_count_stops_at_one_hundred()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        await _factory.AddNotificationsAsync(me.Id, other.Id, group, 130, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var list = await client.GetNotificationsAsync();

        Assert.Equal(100, list.UnreadCountOf());
        Assert.Equal(30, list.ItemsOf().Count);
    }

    [Fact]
    public async Task Read_notifications_do_not_count()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        await _factory.AddNotificationsAsync(me.Id, other.Id, group, 10, LongAgo, isRead: i => i < 7);
        using var client = _factory.CreateClientFor(me);

        Assert.Equal(3, (await client.GetNotificationsAsync()).UnreadCountOf());
    }

    // ##### Paging #####

    [Fact]
    public async Task Pages_of_thirty_walk_the_whole_list_once_in_order_with_read_and_unread_mixed()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 70, LongAgo, isRead: i => i % 3 != 0);
        using var client = _factory.CreateClientFor(me);

        var first = await client.GetNotificationsAsync();
        var second = await client.GetNotificationsAsync(before: first.IdsOf()[^1]);
        var third = await client.GetNotificationsAsync(before: second.IdsOf()[^1]);
        var fourth = await client.GetNotificationsAsync(before: third.IdsOf()[^1]);

        Assert.Equal([30, 30, 10, 0], [first.IdsOf().Count, second.IdsOf().Count, third.IdsOf().Count, fourth.IdsOf().Count]);
        Assert.Equal(Enumerable.Reverse(ids), first.IdsOf().Concat(second.IdsOf()).Concat(third.IdsOf()));
    }

    // Many notifications stamped in the same instant: the id breaks the tie, so no page repeats or skips one.
    [Fact]
    public async Task Notifications_stamped_alike_are_split_across_pages_without_repeats_or_gaps()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var same = DateTime.UtcNow.AddDays(-1);
        await _factory.ExecuteDbAsync(async db =>
        {
            db.Notifications.AddRange(Enumerable.Range(0, 45).Select(i => new Notification
            {
                Id = Guid.NewGuid(), UserId = me.Id, ActorId = other.Id, ConversationId = group,
                Type = NotificationType.Mention, IsRead = i % 2 == 0, CreatedAt = same,
            }));
            await db.SaveChangesAsync();
        });
        using var client = _factory.CreateClientFor(me);

        var first = await client.GetNotificationsAsync();
        var second = await client.GetNotificationsAsync(before: first.IdsOf()[^1]);

        var seen = first.IdsOf().Concat(second.IdsOf()).ToList();
        Assert.Equal(45, seen.Count);
        Assert.Equal(45, seen.Distinct().Count());
        var rows = await _factory.NotificationRowsAsync(me.Id);
        Assert.Equal(rows.Select(n => n.Id).Order(), seen.Order());
    }

    [Fact]
    public async Task A_cursor_that_is_not_the_callers_notification_is_refused_like_one_that_does_not_exist()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var theirs = await _factory.AddNotificationsAsync(other.Id, me.Id, group, 1, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var foreign = await client.GetNotificationsResponseAsync(theirs[0]);
        var unknown = await client.GetNotificationsResponseAsync(Guid.NewGuid());

        foreach (var response in new[] { foreign, unknown })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.ReadProblemAsync();
            Assert.Equal("Unknown notification cursor.", problem.Detail);
            Assert.True(problem.Errors!.Value.TryGetProperty("Before", out _));
        }
    }

    [Fact]
    public async Task A_cursor_that_is_not_a_guid_is_the_generic_unreadable_request()
    {
        var (me, _, _) = await SetUpEmptyAsync();
        using var client = _factory.CreateClientFor(me);

        var response = await client.GetAsync("/api/notifications?before=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_answer_is_401()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetNotificationsResponseAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.MarkNotificationsReadResponseAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DismissNotificationResponseAsync(Guid.NewGuid())).StatusCode);
    }

    // ##### Marking read #####

    [Fact]
    public async Task The_named_notifications_are_marked_read_and_the_others_are_left_alone()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 4, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var response = await client.MarkNotificationsReadResponseAsync([ids[1], ids[3]]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rows = await _factory.NotificationRowsAsync(me.Id);
        Assert.Equal([false, true, false, true], rows.Select(n => n.IsRead));
        Assert.Equal(2, (await client.GetNotificationsAsync()).UnreadCountOf());
    }

    [Fact]
    public async Task Leaving_the_ids_out_marks_every_one_of_the_callers_notifications_read_and_nobody_elses()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        await _factory.AddNotificationsAsync(me.Id, other.Id, group, 5, LongAgo);
        await _factory.AddNotificationsAsync(other.Id, me.Id, group, 3, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var response = await client.MarkNotificationsReadResponseAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.All(await _factory.NotificationRowsAsync(me.Id), n => Assert.True(n.IsRead));
        Assert.All(await _factory.NotificationRowsAsync(other.Id), n => Assert.False(n.IsRead));
    }

    // What the tabs are told is what changed for the caller, even when someone else has unread notifications too.
    [Fact]
    public async Task Marking_everything_read_reports_only_the_callers_own_notifications()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var mine = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 3, LongAgo);
        await _factory.AddNotificationsAsync(other.Id, me.Id, group, 2, LongAgo);
        using var client = _factory.CreateClientFor(me);
        await using var tab = await HubClient.ConnectAsync(_factory, me);

        await client.MarkNotificationsReadResponseAsync();

        Assert.Equal(mine.Order(), (await tab.WaitForNotificationsReadAsync(_ => true)).Order());
    }

    // An empty list names nothing. It must not be read as "all".
    [Fact]
    public async Task An_empty_list_of_ids_marks_nothing()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        await _factory.AddNotificationsAsync(me.Id, other.Id, group, 3, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var response = await client.MarkNotificationsReadResponseAsync([]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.All(await _factory.NotificationRowsAsync(me.Id), n => Assert.False(n.IsRead));
    }

    [Fact]
    public async Task Someone_elses_ids_are_ignored_and_change_nothing()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var mine = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 1, LongAgo);
        var theirs = await _factory.AddNotificationsAsync(other.Id, me.Id, group, 1, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var response = await client.MarkNotificationsReadResponseAsync([theirs[0], mine[0], Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(Assert.Single(await _factory.NotificationRowsAsync(me.Id)).IsRead);
        Assert.False(Assert.Single(await _factory.NotificationRowsAsync(other.Id)).IsRead);
    }

    [Fact]
    public async Task Marking_again_is_not_an_error_and_changes_nothing()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 2, LongAgo);
        using var client = _factory.CreateClientFor(me);
        await client.MarkNotificationsReadResponseAsync(ids);

        var again = await client.MarkNotificationsReadResponseAsync(ids);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.All(await _factory.NotificationRowsAsync(me.Id), n => Assert.True(n.IsRead));
    }

    [Fact]
    public async Task More_than_one_hundred_ids_or_an_empty_id_is_refused()
    {
        var (me, _, _) = await SetUpEmptyAsync();
        using var client = _factory.CreateClientFor(me);

        var tooMany = await client.MarkNotificationsReadResponseAsync(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()));
        var empty = await client.MarkNotificationsReadResponseAsync([Guid.Empty]);
        var exactly = await client.MarkNotificationsReadResponseAsync(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, exactly.StatusCode);
    }

    [Fact]
    public async Task All_of_the_callers_open_tabs_are_told_which_notifications_were_marked_and_no_one_else_is()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 3, LongAgo);
        using var client = _factory.CreateClientFor(me);
        await using var thisTab = await HubClient.ConnectAsync(_factory, me);
        await using var otherTab = await HubClient.ConnectAsync(_factory, me);
        await using var theirTab = await HubClient.ConnectAsync(_factory, other);

        await client.MarkNotificationsReadResponseAsync([ids[0], ids[2]]);

        foreach (var tab in new[] { thisTab, otherTab })
        {
            var told = await tab.WaitForNotificationsReadAsync(_ => true);
            Assert.Equal(new[] { ids[0], ids[2] }.Order(), told.Order());
        }

        Assert.False(await theirTab.ReceivedNotificationsReadAsync());
    }

    [Fact]
    public async Task Nobody_is_told_when_nothing_changed()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 1, LongAgo, isRead: _ => true);
        using var client = _factory.CreateClientFor(me);
        await using var tab = await HubClient.ConnectAsync(_factory, me);

        await client.MarkNotificationsReadResponseAsync(ids);
        await client.MarkNotificationsReadResponseAsync([Guid.NewGuid()]);

        Assert.False(await tab.ReceivedNotificationsReadAsync());
    }

    // ##### Dismissing #####

    [Fact]
    public async Task A_dismissed_notification_is_gone_and_stops_counting()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 3, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var response = await client.DismissNotificationResponseAsync(ids[1]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var list = await client.GetNotificationsAsync();
        Assert.Equal([ids[2], ids[0]], list.IdsOf());
        Assert.Equal(2, list.UnreadCountOf());
    }

    [Fact]
    public async Task Someone_elses_notification_and_an_unknown_one_get_the_same_404_and_nothing_is_removed()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var theirs = await _factory.AddNotificationsAsync(other.Id, me.Id, group, 1, LongAgo);
        using var client = _factory.CreateClientFor(me);

        var foreign = await client.DismissNotificationResponseAsync(theirs[0]);
        var unknown = await client.DismissNotificationResponseAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Not Found", (await foreign.ReadProblemAsync()).Title);
        Assert.Single(await _factory.NotificationRowsAsync(other.Id));
    }

    [Fact]
    public async Task Dismissing_twice_is_a_404_the_second_time()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 1, LongAgo);
        using var client = _factory.CreateClientFor(me);
        await client.DismissNotificationResponseAsync(ids[0]);

        var again = await client.DismissNotificationResponseAsync(ids[0]);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task All_of_the_callers_open_tabs_are_told_about_a_dismissal_and_no_one_else_is()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var ids = await _factory.AddNotificationsAsync(me.Id, other.Id, group, 1, LongAgo);
        using var client = _factory.CreateClientFor(me);
        await using var thisTab = await HubClient.ConnectAsync(_factory, me);
        await using var otherTab = await HubClient.ConnectAsync(_factory, me);
        await using var theirTab = await HubClient.ConnectAsync(_factory, other);

        await client.DismissNotificationResponseAsync(ids[0]);

        await thisTab.WaitForNotificationDismissedAsync(ids[0]);
        await otherTab.WaitForNotificationDismissedAsync(ids[0]);
        Assert.False(await theirTab.ReceivedNotificationDismissedAsync());
    }

    [Fact]
    public async Task A_dismissal_that_is_refused_tells_nobody()
    {
        var (me, other, group) = await SetUpEmptyAsync();
        var theirs = await _factory.AddNotificationsAsync(other.Id, me.Id, group, 1, LongAgo);
        using var client = _factory.CreateClientFor(me);
        await using var theirTab = await HubClient.ConnectAsync(_factory, other);

        await client.DismissNotificationResponseAsync(theirs[0]);

        Assert.False(await theirTab.ReceivedNotificationDismissedAsync());
    }
}
