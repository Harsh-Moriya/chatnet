using System.Net;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Messaging;

// The changes feed: what changed in a conversation after a point, for a client that keeps the messages it has read. Every message that
// was created, edited or deleted after the point, in the order of its latest change and in its current state, within the time the
// caller was a member.
[Collection(IntegrationTestCollection.Name)]
public class ChangesFeedTests
{
    private readonly ChatApiFactory _factory;

    public ChangesFeedTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid Conversation)> ChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        return (alice, bob, conversation);
    }

    private static string[] TextsOf(IEnumerable<JsonElement> messages) => messages.Select(m => m.ContentOf() ?? "(deleted)").ToArray();

    // ##### What it returns #####

    [Fact]
    public async Task Everything_from_the_start_comes_in_the_order_the_changes_were_made_with_the_cursor_at_the_newest()
    {
        var (alice, bob, conversation) = await ChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        await aliceClient.SendMessageAsync(conversation, "one");
        await bobClient.SendMessageAsync(conversation, "two");
        await aliceClient.SendMessageAsync(conversation, "three");

        var page = await aliceClient.GetChangesAsync(conversation, since: 0);

        Assert.Equal(["one", "two", "three"], TextsOf(page.Changes));
        Assert.Equal([1L, 2L, 3L], page.Changes.Select(m => m.ChangeSeqOf()).ToArray());
        Assert.Equal(3L, page.Cursor);
        Assert.False(page.More);
    }

    [Fact]
    public async Task Reading_from_a_point_returns_only_what_changed_after_it()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "one");
        await client.SendMessageAsync(conversation, "two");
        await client.SendMessageAsync(conversation, "three");

        var page = await client.GetChangesAsync(conversation, since: 1);

        Assert.Equal(["two", "three"], TextsOf(page.Changes));
    }

    [Fact]
    public async Task An_edited_message_appears_once_as_it_is_now_after_the_changes_that_came_before_it()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.SendMessageAsync(conversation, "one");
        await client.SendMessageAsync(conversation, "two");
        await client.EditMessageAsync(conversation, first.MessageIdOf(), "one, edited");

        var page = await client.GetChangesAsync(conversation, since: 0);

        Assert.Equal(["two", "one, edited"], TextsOf(page.Changes));
        Assert.Equal(1, page.Changes[1].GetProperty("editCount").GetInt32());
        Assert.Equal(3L, page.Changes[1].ChangeSeqOf());
    }

    [Fact]
    public async Task A_client_that_had_seen_a_message_is_told_of_its_edit_by_reading_from_where_it_was()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.SendMessageAsync(conversation, "one");
        var seen = (await client.GetChangesAsync(conversation, since: 0)).Cursor;
        await client.EditMessageAsync(conversation, first.MessageIdOf(), "one, edited");

        var page = await client.GetChangesAsync(conversation, since: seen);

        Assert.Equal(["one, edited"], TextsOf(page.Changes));
        Assert.Equal(first.MessageIdOf(), page.Changes[0].MessageIdOf());
    }

    [Fact]
    public async Task A_deleted_message_comes_without_its_text_or_files()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.SendMessageAsync(conversation, "one");
        await client.SendMessageAsync(conversation, "two");
        await client.DeleteMessageAsync(conversation, first.MessageIdOf());

        var page = await client.GetChangesAsync(conversation, since: 0);

        Assert.Equal(["two", "(deleted)"], TextsOf(page.Changes));
        var tombstone = page.Changes[1];
        Assert.True(tombstone.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("ciphertext").ValueKind);
        Assert.Empty(tombstone.GetProperty("attachments").EnumerateArray());
        Assert.Equal(3L, tombstone.ChangeSeqOf());
    }

    [Fact]
    public async Task The_files_of_a_message_come_with_links_like_in_the_history()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "with files", DateTime.UtcNow, fileCount: 2);

        var page = await client.GetChangesAsync(conversation, since: 0);

        var files = page.Changes.Single().GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, file => Assert.StartsWith("http", file.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Nothing_after_the_newest_change_is_an_empty_page_with_the_cursor_where_it_was()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "one");
        await client.SendMessageAsync(conversation, "two");

        var page = await client.GetChangesAsync(conversation, since: 2);

        Assert.Empty(page.Changes);
        Assert.Equal(2L, page.Cursor);
        Assert.False(page.More);
    }

    [Fact]
    public async Task A_conversation_where_nothing_was_said_is_an_empty_page_at_zero()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var page = await client.GetChangesAsync(conversation, since: 0);

        Assert.Empty(page.Changes);
        Assert.Equal(0L, page.Cursor);
        Assert.False(page.More);
    }

    [Fact]
    public async Task What_the_feed_holds_is_what_the_history_holds_after_sends_edits_and_deletes()
    {
        var (alice, bob, conversation) = await ChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var one = await aliceClient.SendMessageAsync(conversation, "one");
        var two = await bobClient.SendMessageAsync(conversation, "two");
        await aliceClient.SendMessageAsync(conversation, "three");
        await aliceClient.EditMessageAsync(conversation, one.MessageIdOf(), "one, edited");
        await bobClient.DeleteMessageAsync(conversation, two.MessageIdOf());

        var fromFeed = (await aliceClient.ReadAllChangesAsync(conversation)).ToDictionary(m => m.MessageIdOf());
        var fromHistory = (await aliceClient.GetHistoryAsync(conversation)).ToDictionary(m => m.MessageIdOf());

        Assert.Equal(fromHistory.Keys.OrderBy(k => k), fromFeed.Keys.OrderBy(k => k));
        foreach (var (id, message) in fromHistory)
        {
            Assert.Equal(message.ContentOf(), fromFeed[id].ContentOf());
            Assert.Equal(message.GetProperty("editCount").GetInt32(), fromFeed[id].GetProperty("editCount").GetInt32());
            Assert.Equal(message.GetProperty("isDeleted").GetBoolean(), fromFeed[id].GetProperty("isDeleted").GetBoolean());
            Assert.Equal(message.ChangeSeqOf(), fromFeed[id].ChangeSeqOf());
        }
    }

    // ##### Pages #####

    [Theory]
    [InlineData(99, false, 99)]
    [InlineData(100, false, 100)]
    [InlineData(101, true, 100)]
    public async Task A_page_holds_at_most_a_hundred_changes_and_says_whether_there_are_more(int total, bool more, int onFirstPage)
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.AddMessagesAsync(conversation, alice.Id, total, DateTime.UtcNow, TimeSpan.FromMilliseconds(1));

        var page = await client.GetChangesAsync(conversation, since: 0);

        Assert.Equal(onFirstPage, page.Changes.Count);
        Assert.Equal(more, page.More);
        Assert.Equal(more ? 100L : total, page.Cursor);
    }

    [Fact]
    public async Task Following_the_cursor_reads_every_change_once_and_ends_at_the_newest()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.AddMessagesAsync(conversation, alice.Id, 250, DateTime.UtcNow, TimeSpan.FromMilliseconds(1));

        var first = await client.GetChangesAsync(conversation, since: 0);
        var second = await client.GetChangesAsync(conversation, since: first.Cursor);
        var third = await client.GetChangesAsync(conversation, since: second.Cursor);

        Assert.Equal([100, 100, 50], new[] { first.Changes.Count, second.Changes.Count, third.Changes.Count });
        Assert.Equal([true, true, false], new[] { first.More, second.More, third.More });
        Assert.Equal([100L, 200L, 250L], new[] { first.Cursor, second.Cursor, third.Cursor });
        Assert.Equal(Enumerable.Range(1, 250).Select(i => (long)i), first.Changes.Concat(second.Changes).Concat(third.Changes).Select(m => m.ChangeSeqOf()));
    }

    // ##### Who may read what #####

    [Fact]
    public async Task Someone_who_is_not_a_member_is_refused_like_for_the_history()
    {
        var (_, _, conversation) = await ChatAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = _factory.CreateClientFor(stranger);

        var response = await client.GetChangesResponseAsync(conversation, 0);
        var history = await client.GetHistoryResponseAsync(conversation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal((await history.ReadProblemAsync()).Detail, (await response.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_is_refused_the_same_way()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetChangesResponseAsync(Guid.NewGuid(), 0);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_answer_is_401()
    {
        var (_, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetChangesResponseAsync(conversation, 0)).StatusCode);
    }

    [Fact]
    public async Task Someone_who_joined_later_reads_only_what_was_sent_after_they_joined_even_when_an_older_message_is_edited()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var early = await _factory.RegisterUserAsync("early");
        var late = await _factory.RegisterUserAsync("late");
        using var ownerClient = _factory.CreateClientFor(owner);
        var group = (await ownerClient.CreateGroupAsync("Team", early.Id)).IdOf();
        var before = await ownerClient.SendMessageAsync(group, "said before");
        await Task.Delay(20);
        await ownerClient.AddMembersAsync(group, late.Id);
        await Task.Delay(20);
        await ownerClient.SendMessageAsync(group, "said after");
        await ownerClient.EditMessageAsync(group, before.MessageIdOf(), "said before, edited");
        using var lateClient = _factory.CreateClientFor(late);

        var page = await lateClient.GetChangesAsync(group, since: 0);

        Assert.Equal(["said after"], TextsOf(page.Changes));
        Assert.Equal(3L, page.Cursor);
    }

    [Fact]
    public async Task Someone_who_left_reads_what_was_sent_while_they_were_a_member_in_its_current_state_and_nothing_after()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var leaver = await _factory.RegisterUserAsync("leaver");
        using var ownerClient = _factory.CreateClientFor(owner);
        var group = (await ownerClient.CreateGroupAsync("Team", leaver.Id)).IdOf();
        var kept = await ownerClient.SendMessageAsync(group, "while they were in");
        await Task.Delay(20);
        using var leaverClient = _factory.CreateClientFor(leaver);
        Assert.Equal(HttpStatusCode.OK, (await leaverClient.LeaveResponseAsync(group)).StatusCode);
        await Task.Delay(20);

        // Someone left, so the keys must be replaced before anything more is said.
        await ownerClient.StoreNextEpochAsync(group);
        await ownerClient.SendMessageAsync(group, "after they left");
        await ownerClient.EditMessageAsync(group, kept.MessageIdOf(), "while they were in, edited");

        var feed = await leaverClient.GetChangesAsync(group, since: 0);
        var history = await leaverClient.GetHistoryAsync(group);

        Assert.Equal(["while they were in, edited"], TextsOf(feed.Changes));
        Assert.Equal(TextsOf(history), TextsOf(feed.Changes));
    }

    // ##### What it refuses #####

    [Fact]
    public async Task A_point_past_the_newest_change_is_a_conflict_that_says_so()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "one");

        var response = await client.GetChangesResponseAsync(conversation, 2);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("changes-cursor-ahead", problem.Code);
    }

    [Fact]
    public async Task The_newest_change_itself_is_not_ahead()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "one");

        Assert.Equal(HttpStatusCode.OK, (await client.GetChangesResponseAsync(conversation, 1)).StatusCode);
    }

    [Fact]
    public async Task A_negative_point_is_refused()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetChangesResponseAsync(conversation, -1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("negative", (await response.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_request_without_a_point_is_refused()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetChangesResponseAsync(conversation, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("since", (await response.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_point_that_is_not_a_number_is_refused()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync($"/api/conversations/{conversation}/changes?since=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ##### Logging #####

    [Fact]
    public async Task A_refused_read_is_logged_with_ids_and_a_successful_one_logs_nothing_of_what_was_said()
    {
        var capture = new CapturingLoggerProvider();
        var (alice, _, conversation) = await ChatAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var aliceClient = _factory.CreateClientFor(alice);
        var sent = await aliceClient.SendMessageAsync(conversation, "words that must stay out of the log");
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var strangerClient = host.CreateClient();
        strangerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", stranger.Token);
        using var memberClient = host.CreateClient();
        memberClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);

        await strangerClient.GetChangesResponseAsync(conversation, 0);
        await memberClient.GetChangesAsync(conversation, 0);

        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains("the changes of conversation"));
        Assert.Contains(stranger.Id.ToString(), warning);
        Assert.Contains(conversation.ToString(), warning);
        var blob = sent.GetProperty("ciphertext").GetString()!;
        Assert.DoesNotContain(capture.Entries, e => e.Contains(blob) || e.Contains("words that must stay out"));
    }
}
