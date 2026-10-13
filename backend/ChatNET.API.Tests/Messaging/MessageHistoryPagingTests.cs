using System.Net;
using System.Text.Json;
using ChatNET.API.Common.Time;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Messaging;

// Paging is how a client reads past the latest 50 messages (before) and catches up on what it missed
// while disconnected (after). The cursor is a message id; the server turns it into the position
// (CreatedAt, Id) so that messages sharing a timestamp are never skipped or repeated.
[Collection(IntegrationTestCollection.Name)]
public class MessageHistoryPagingTests
{
    private const int PageSize = 50;
    private const string UnknownCursor = "Unknown message cursor.";

    private readonly ChatApiFactory _factory;

    public MessageHistoryPagingTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### Reading backwards #####

    [Fact]
    public async Task Paging_back_from_the_latest_page_returns_every_message_exactly_once()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, 120, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);

        var walked = await WalkBackAsync(client, conversation);

        Assert.Equal(Enumerable.Range(0, 120).Select(i => $"message {i}"), walked.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task A_page_before_a_message_is_the_50_just_older_ones_oldest_first()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, 120, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);
        var latest = await client.GetHistoryAsync(conversation);

        var page = await client.GetHistoryAsync(conversation, before: latest[0].MessageIdOf());

        Assert.Equal(PageSize, page.Count);
        Assert.Equal(Enumerable.Range(20, 50).Select(i => $"message {i}"), page.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task The_last_page_before_the_oldest_message_is_short_and_then_empty()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, 60, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);
        var latest = await client.GetHistoryAsync(conversation);

        var older = await client.GetHistoryAsync(conversation, before: latest[0].MessageIdOf());
        var beforeTheOldest = await client.GetHistoryAsync(conversation, before: older[0].MessageIdOf());

        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"message {i}"), older.Select(m => m.ContentOf()));
        Assert.Empty(beforeTheOldest);
    }

    // The case a plain "CreatedAt < anchor" cursor gets wrong: a page boundary that falls inside a
    // group of messages with one timestamp would drop or repeat the rest of the group.
    [Fact]
    public async Task Messages_with_the_same_timestamp_are_neither_skipped_nor_repeated_across_pages()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        var start = DateTime.UtcNow.AddHours(-2).TruncateToMicroseconds();
        await _factory.AddMessagesAsync(conversation, bob.Id, 40, start, TimeSpan.Zero, "a");
        await _factory.AddMessagesAsync(conversation, bob.Id, 40, start.AddMinutes(1), TimeSpan.Zero, "b");
        await _factory.AddMessagesAsync(conversation, bob.Id, 40, start.AddMinutes(2), TimeSpan.Zero, "c");
        using var client = _factory.CreateClientFor(alice);

        var walked = await WalkBackAsync(client, conversation);

        Assert.Equal(await StoredOrderAsync(conversation), walked.Select(m => m.MessageIdOf()));
    }

    // ##### Reading forwards #####

    [Fact]
    public async Task Paging_forward_from_the_oldest_message_returns_every_later_message_exactly_once()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, 120, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);
        var oldest = (await StoredOrderAsync(conversation))[0];

        var walked = await WalkForwardAsync(client, conversation, oldest);

        Assert.Equal(Enumerable.Range(1, 119).Select(i => $"message {i}"), walked.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task Forward_paging_across_a_group_of_equal_timestamps_is_complete()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        var start = DateTime.UtcNow.AddHours(-2).TruncateToMicroseconds();
        await _factory.AddMessagesAsync(conversation, bob.Id, 40, start, TimeSpan.Zero, "a");
        await _factory.AddMessagesAsync(conversation, bob.Id, 40, start.AddMinutes(1), TimeSpan.Zero, "b");
        await _factory.AddMessagesAsync(conversation, bob.Id, 40, start.AddMinutes(2), TimeSpan.Zero, "c");
        using var client = _factory.CreateClientFor(alice);
        var stored = await StoredOrderAsync(conversation);

        var walked = await WalkForwardAsync(client, conversation, stored[0]);

        Assert.Equal(stored.Skip(1), walked.Select(m => m.MessageIdOf()));
    }

    [Fact]
    public async Task A_page_after_the_newest_message_is_empty()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, 5, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);
        var latest = await client.GetHistoryAsync(conversation);

        var page = await client.GetHistoryAsync(conversation, after: latest[^1].MessageIdOf());

        Assert.Empty(page);
    }

    [Fact]
    public async Task Pages_keep_the_documented_message_shape_and_hide_deleted_content()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        var start = DateTime.UtcNow.AddHours(-1);
        await _factory.AddMessageAsync(conversation, bob.Id, "anchor", start.AddMinutes(2));
        await _factory.AddMessageAsync(conversation, bob.Id, "secret that was deleted", start.AddMinutes(1), deleted: true);
        await _factory.AddMessageAsync(conversation, bob.Id, "oldest", start);
        using var client = _factory.CreateClientFor(alice);
        var anchor = (await client.GetHistoryAsync(conversation))[^1].MessageIdOf();

        var page = await client.GetHistoryAsync(conversation, before: anchor);

        Assert.Equal(["oldest", null], page.Select(m => m.ContentOf()));
        Assert.DoesNotContain("secret that was deleted", JsonSerializer.Serialize(page));
        Assert.Equivalent(
            new[] { "id", "conversationId", "senderId", "senderDisplayName", "content", "createdAt", "clientMessageId" },
            page[0].EnumerateObject().Select(p => p.Name));
    }

    // ##### Which messages a cursor may point at #####

    [Fact]
    public async Task Paging_back_never_crosses_the_time_the_member_joined()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Late joiner", bob.Id, carol.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);
        var joined = DateTime.UtcNow.AddMinutes(-30).TruncateToMicroseconds();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == carol.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, joined)));
        await _factory.AddMessagesAsync(group, alice.Id, 70, joined.AddMinutes(-120), TimeSpan.FromSeconds(10), "before");
        await _factory.AddMessagesAsync(group, alice.Id, 70, joined, TimeSpan.FromSeconds(10), "after");

        var forCarol = await WalkBackAsync(carolClient, group);
        var forAlice = await WalkBackAsync(aliceClient, group);

        Assert.Equal(Enumerable.Range(0, 70).Select(i => $"after {i}"), forCarol.Select(m => m.ContentOf()));
        Assert.Equal(140, forAlice.Count);
    }

    [Fact]
    public async Task A_cursor_from_before_the_member_joined_is_refused()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Late joiner", bob.Id, carol.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);
        var joined = DateTime.UtcNow.AddMinutes(-30).TruncateToMicroseconds();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == carol.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, joined)));
        await _factory.AddMessageAsync(group, alice.Id, "before carol", joined.AddMinutes(-10));
        await _factory.AddMessageAsync(group, alice.Id, "after carol", joined.AddMinutes(10));
        var hidden = (await aliceClient.GetHistoryAsync(group))[0].MessageIdOf();

        var before = await carolClient.GetHistoryResponseAsync(group, before: hidden);
        var after = await carolClient.GetHistoryResponseAsync(group, after: hidden);

        await AssertUnknownCursorAsync(before);
        await AssertUnknownCursorAsync(after);
    }

    [Fact]
    public async Task A_cursor_that_does_not_exist_is_refused()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        await AssertUnknownCursorAsync(await client.GetHistoryResponseAsync(conversation, before: Guid.NewGuid()));
        await AssertUnknownCursorAsync(await client.GetHistoryResponseAsync(conversation, after: Guid.NewGuid()));
    }

    // A message of another conversation is a valid id but not a valid position here; accepting it
    // would let one conversation's timing be probed from another.
    [Fact]
    public async Task A_cursor_from_another_conversation_is_refused_the_same_way_as_an_unknown_one()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var withBob = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var withCarol = (await client.CreateDirectAsync(carol.Id)).IdOf();
        var elsewhere = (await client.SendMessageAsync(withCarol, "in the Carol chat")).MessageIdOf();

        var response = await client.GetHistoryResponseAsync(withBob, before: elsewhere);

        await AssertUnknownCursorAsync(response);
    }

    [Fact]
    public async Task Asking_for_both_directions_at_once_is_a_validation_error()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var message = (await client.SendMessageAsync(conversation, "anchor")).MessageIdOf();

        var response = await client.GetHistoryResponseAsync(conversation, before: message, after: message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Validation Failed", problem.Title);
        Assert.Contains("before", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cursor_that_is_not_a_guid_is_a_bad_request()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var before = await client.GetAsync($"/api/conversations/{conversation}/messages?before=not-a-guid");
        var after = await client.GetAsync($"/api/conversations/{conversation}/messages?after=1234");

        Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, after.StatusCode);
        Assert.Equal(400, (await before.ReadProblemAsync()).Status);
    }

    [Fact]
    public async Task An_empty_cursor_is_refused_like_any_other_unknown_one()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        await AssertUnknownCursorAsync(await client.GetHistoryResponseAsync(conversation, before: Guid.Empty));
        await AssertUnknownCursorAsync(await client.GetHistoryResponseAsync(conversation, after: Guid.Empty));
    }

    // ##### Who may page #####

    [Fact]
    public async Task A_non_member_is_refused_before_the_cursor_is_looked_at()
    {
        var (_, _, conversation) = await DirectChatAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var intruderClient = _factory.CreateClientFor(intruder);

        var response = await intruderClient.GetHistoryResponseAsync(conversation, before: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ##### Helpers #####

    // No test here has more than a few hundred messages. A walk that needs more pages than this is
    // not making progress (a cursor that is ignored returns the same page for ever), so it fails.
    private const int MaxPages = 20;

    // Follows "before" from the latest page to the start of the history and returns everything read,
    // oldest first. Fails if a page is longer than the page size.
    private static async Task<List<JsonElement>> WalkBackAsync(HttpClient client, Guid conversation)
    {
        var all = new List<JsonElement>();
        var page = await client.GetHistoryAsync(conversation);

        for (var pages = 1; page.Count > 0; pages++)
        {
            Assert.True(pages <= MaxPages, "Paging back never reached the start of the history.");
            Assert.True(page.Count <= PageSize);
            all.InsertRange(0, page);
            page = await client.GetHistoryAsync(conversation, before: page[0].MessageIdOf());
        }

        return all;
    }

    private static async Task<List<JsonElement>> WalkForwardAsync(HttpClient client, Guid conversation, Guid from)
    {
        var all = new List<JsonElement>();
        var page = await client.GetHistoryAsync(conversation, after: from);

        for (var pages = 1; page.Count > 0; pages++)
        {
            Assert.True(pages <= MaxPages, "Paging forward never reached the end of the history.");
            Assert.True(page.Count <= PageSize);
            all.AddRange(page);
            page = await client.GetHistoryAsync(conversation, after: page[^1].MessageIdOf());
        }

        return all;
    }

    // The order Postgres itself gives the messages: the oracle the API's paging is checked against.
    private Task<List<Guid>> StoredOrderAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.ConversationId == conversation)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync());

    private static async Task AssertUnknownCursorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Validation Failed", problem.Title);
        Assert.Equal(UnknownCursor, problem.Detail);
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid ConversationId)> DirectChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(conversation);
        return (alice, bob, conversation);
    }
}
