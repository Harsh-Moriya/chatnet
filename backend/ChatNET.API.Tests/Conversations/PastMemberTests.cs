using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Common.Time;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Conversations;

// What someone who left a group (or was removed from it) still has: the conversation, read-only, up to the moment they
// left. The list, the conversation itself, its history and its files show that part and no more; everything that writes,
// and the list of its people, is refused.
[Collection(IntegrationTestCollection.Name)]
public class PastMemberTests
{
    private readonly ChatApiFactory _factory;

    public PastMemberTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Alice owns a group with Bob. Messages before and after the moment Bob left are arranged one minute apart.
    private sealed record Setup(
        Guid Group, TestUser Alice, TestUser Bob, DateTime LeftAt, List<Guid> Before, List<Guid> After);

    private async Task<Setup> LeftGroupAsync(int before = 3, int after = 3)
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        var group = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);

        var leftAt = DateTime.UtcNow.AddMinutes(-30).TruncateToMicroseconds();
        var earlier = new List<Guid>();
        var later = new List<Guid>();
        for (var i = 1; i <= before; i++)
            earlier.Add(await AddAsync(group, alice, $"before {i}", leftAt.AddMinutes(-(before - i + 1))));
        for (var i = 1; i <= after; i++)
            later.Add(await AddAsync(group, alice, $"after {i}", leftAt.AddMinutes(i)));

        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, (DateTime?)leftAt)));

        return new Setup(group, alice, bob, leftAt, earlier, later);
    }

    private async Task<Guid> AddAsync(Guid group, TestUser sender, string content, DateTime at)
    {
        await _factory.AddMessageAsync(group, sender.Id, content, at);
        return (await _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.ConversationId == group && m.Content == content).Select(m => m.Id).SingleAsync()));
    }

    private static List<string?> Contents(IEnumerable<JsonElement> messages) => messages.Select(m => m.ContentOf()).ToList();

    // ##### The list #####

    [Fact]
    public async Task The_list_shows_the_group_with_the_last_message_from_before_they_left_and_nothing_unread()
    {
        var s = await LeftGroupAsync();
        using var bob = _factory.CreateClientFor(s.Bob);

        var view = Assert.Single(await bob.ListConversationsAsync());

        Assert.Equal(s.Group, view.IdOf());
        Assert.Equal("before 3", view.GetProperty("lastMessage").GetProperty("content").GetString());
        Assert.Equal(0, view.UnreadOf());
        Assert.Equal(s.LeftAt, view.GetProperty("leftAt").GetDateTime());
    }

    [Fact]
    public async Task A_group_with_nothing_said_before_they_left_has_no_preview()
    {
        var s = await LeftGroupAsync(before: 0);
        using var bob = _factory.CreateClientFor(s.Bob);

        var view = Assert.Single(await bob.ListConversationsAsync());

        Assert.Equal(JsonValueKind.Null, view.GetProperty("lastMessage").ValueKind);
    }

    [Fact]
    public async Task What_is_unread_in_a_group_still_joined_is_still_counted()
    {
        var s = await LeftGroupAsync();
        using var alice = _factory.CreateClientFor(s.Alice);

        var view = Assert.Single(await alice.ListConversationsAsync());

        Assert.Equal(JsonValueKind.Null, view.GetProperty("leftAt").ValueKind);
        Assert.Equal("after 3", view.GetProperty("lastMessage").GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_group_is_ordered_by_when_it_was_left_when_the_others_went_on_after()
    {
        var s = await LeftGroupAsync();
        using var bob = _factory.CreateClientFor(s.Bob);
        using var bobsOther = _factory.CreateClientFor(s.Bob);
        var other = (await bobsOther.CreateDirectAsync(s.Alice.Id)).IdOf();
        var now = DateTime.UtcNow;
        await _factory.ExecuteDbAsync(async db =>
        {
            await db.Conversations.Where(c => c.Id == s.Group).ExecuteUpdateAsync(c => c.SetProperty(x => x.UpdatedAt, now));
            await db.Conversations.Where(c => c.Id == other).ExecuteUpdateAsync(c => c.SetProperty(x => x.UpdatedAt, now.AddMinutes(-10)));
        });

        var list = await bob.ListConversationsAsync();

        // The group was last active just now, but Bob left half an hour ago: for him it is older than the other chat.
        Assert.Equal([other, s.Group], list.Select(c => c.IdOf()));
        Assert.Equal(s.LeftAt, list[1].GetProperty("updatedAt").GetDateTime());
    }

    [Fact]
    public async Task A_group_that_was_quiet_after_they_left_keeps_its_own_last_activity_as_its_place()
    {
        var s = await LeftGroupAsync();
        var quiet = s.LeftAt.AddHours(-2);
        await _factory.ExecuteDbAsync(db => db.Conversations
            .Where(c => c.Id == s.Group).ExecuteUpdateAsync(c => c.SetProperty(x => x.UpdatedAt, quiet)));
        using var bob = _factory.CreateClientFor(s.Bob);

        var view = Assert.Single(await bob.ListConversationsAsync());

        Assert.Equal(quiet, view.GetProperty("updatedAt").GetDateTime());
    }

    // ##### The conversation and its history #####

    [Fact]
    public async Task The_conversation_itself_is_still_there_and_says_when_they_left()
    {
        var s = await LeftGroupAsync();
        using var bob = _factory.CreateClientFor(s.Bob);

        var response = await bob.GetAsync($"/api/conversations/{s.Group}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(s.LeftAt, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leftAt").GetDateTime());
    }

    [Fact]
    public async Task Someone_who_was_never_in_the_group_gets_nothing_of_it()
    {
        var s = await LeftGroupAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = _factory.CreateClientFor(stranger);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/conversations/{s.Group}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetHistoryResponseAsync(s.Group)).StatusCode);
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task The_history_is_what_was_said_up_to_when_they_left_and_nothing_after()
    {
        var s = await LeftGroupAsync();
        using var bob = _factory.CreateClientFor(s.Bob);

        var latest = await bob.GetHistoryAsync(s.Group);

        Assert.Equal(["before 1", "before 2", "before 3"], Contents(latest));
    }

    [Fact]
    public async Task A_message_sent_at_the_very_moment_they_left_is_still_theirs_to_read()
    {
        var s = await LeftGroupAsync(before: 1, after: 0);
        await AddAsync(s.Group, s.Alice, "at the moment", s.LeftAt);
        using var bob = _factory.CreateClientFor(s.Bob);

        Assert.Equal(["before 1", "at the moment"], Contents(await bob.GetHistoryAsync(s.Group)));
        var view = Assert.Single(await bob.ListConversationsAsync());
        Assert.Equal("at the moment", view.GetProperty("lastMessage").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Paging_back_and_forward_stays_inside_the_part_they_may_read()
    {
        var s = await LeftGroupAsync(before: 5);
        using var bob = _factory.CreateClientFor(s.Bob);

        var older = await bob.GetHistoryAsync(s.Group, before: s.Before[3]);
        var newer = await bob.GetHistoryAsync(s.Group, after: s.Before[1]);

        Assert.Equal(["before 1", "before 2", "before 3"], Contents(older));
        Assert.Equal(["before 3", "before 4", "before 5"], Contents(newer));
    }

    [Fact]
    public async Task A_message_from_after_they_left_is_not_a_cursor_they_may_use()
    {
        var s = await LeftGroupAsync();
        using var bob = _factory.CreateClientFor(s.Bob);

        var response = await bob.GetHistoryResponseAsync(s.Group, before: s.After[0]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown message cursor", (await response.ReadProblemAsync()).Root.ToString());
    }

    [Fact]
    public async Task A_message_deleted_before_they_left_is_still_a_gap_free_deleted_entry()
    {
        var s = await LeftGroupAsync();
        await _factory.ExecuteDbAsync(db => db.Messages.Where(m => m.Id == s.Before[1]).ExecuteUpdateAsync(m =>
            m.SetProperty(x => x.IsDeleted, true).SetProperty(x => x.Content, (string?)null)));
        using var bob = _factory.CreateClientFor(s.Bob);

        var history = await bob.GetHistoryAsync(s.Group);

        Assert.Equal(3, history.Count);
        Assert.True(history[1].GetProperty("isDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task Someone_added_back_reads_from_the_new_join_and_not_the_old_window()
    {
        var s = await LeftGroupAsync();
        using var alice = _factory.CreateClientFor(s.Alice);
        using var bob = _factory.CreateClientFor(s.Bob);

        await alice.AddMembersAsync(s.Group, s.Bob.Id);
        await alice.SendMessageAsync(s.Group, "after they came back");

        var view = Assert.Single(await bob.ListConversationsAsync());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("leftAt").ValueKind);
        Assert.Equal(["after they came back"], Contents(await bob.GetHistoryAsync(s.Group)));
    }

    // ##### Everything that writes is refused #####

    [Fact]
    public async Task Someone_who_left_can_neither_write_nor_change_anything_and_does_not_see_the_people()
    {
        var s = await LeftGroupAsync();
        using var bob = _factory.CreateClientFor(s.Bob);
        var own = await AddAsync(s.Group, s.Bob, "bob said this", s.LeftAt.AddMinutes(-5));

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostMessageAsync(s.Group, "hello?")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PatchMessageAsync(s.Group, own, "edited")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteMessageResponseAsync(s.Group, own)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.MarkReadResponseAsync(s.Group, s.Before[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SetMutedResponseAsync(s.Group, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.RequestUploadAsync(s.Group, "a.png", "image/png", 100)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetMembersResponseAsync(s.Group)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.LeaveResponseAsync(s.Group)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.AddMembersResponseAsync(s.Group, s.Alice.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.RenameResponseAsync(s.Group, "Mine")).StatusCode);
    }

    [Fact]
    public async Task A_connection_made_after_they_left_is_not_put_into_the_group_and_cannot_join_it()
    {
        var s = await LeftGroupAsync();
        using var alice = _factory.CreateClientFor(s.Alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, s.Bob);

        await alice.SendMessageAsync(s.Group, "said after bob left");

        Assert.False(await bobTab.ReceivedMessageAsync(s.Group));
        await Assert.ThrowsAsync<HubException>(() => bobTab.JoinConversationAsync(s.Group));
        await Assert.ThrowsAsync<HubException>(() => bobTab.StartTypingAsync(s.Group));
    }

    // ##### The cost of leaving a group alone #####

    // A group that went on for a long time after someone left must cost them no more to open than one that did not. The
    // upper bound is a range on the column the index is ordered by; as a plain filter it would walk every message sent since.
    [Fact]
    public async Task A_group_with_thousands_of_messages_after_they_left_is_read_at_a_cost_that_does_not_grow()
    {
        const int later = 6000;
        var s = await LeftGroupAsync(before: 60, after: 0);
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Content"", ""CreatedAt"")
            SELECT gen_random_uuid(), {s.Group}, {s.Alice.Id}, 'later', {s.LeftAt} + (g * interval '1 second')
            FROM generate_series(1, {later}) AS g"));
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlRawAsync(@"ANALYZE ""Messages"""));

        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s.Bob.Token);

        recorder.Clear();
        var list = await client.GetAsync("/api/conversations");
        var listQuery = Assert.Single(recorder.Commands);
        recorder.Clear();
        var history = await client.GetAsync($"/api/conversations/{s.Group}/messages");
        var historyQuery = Assert.Single(recorder.Commands, c => c.Sql.Contains("\"Messages\"") && c.Sql.Contains("LIMIT"));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(50, (await history.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        var listRows = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, listQuery, "Messages"));
        var historyRows = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, historyQuery, "Messages"));
        Assert.True(listRows <= 100, $"The list read {listRows} Messages rows for a group with {later} messages after the reader left.");
        Assert.True(historyRows <= 100, $"The history read {historyRows} Messages rows for a group with {later} messages after the reader left.");
    }
}
