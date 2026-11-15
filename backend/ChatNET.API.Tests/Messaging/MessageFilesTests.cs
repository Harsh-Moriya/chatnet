using System.Net;
using ChatNET.API.Common.Time;
using ChatNET.API.Messaging.GetMessageFiles;
using ChatNET.API.Messaging.GetMessageHistory;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Messaging;

// The gallery lists the messages that have files, newest first, a page at a time, from the part of the history the caller may read.
// A message is found by its mark (see Message.HasFiles), so what makes a message appear here is that it was stored with files and
// is not deleted, and the window and the cursor follow the same rules as the history.
[Collection(IntegrationTestCollection.Name)]
public class MessageFilesTests
{
    private readonly ChatApiFactory _factory;

    public MessageFilesTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static DateTime Ago(int minutes) => DateTime.UtcNow.AddMinutes(-minutes).TruncateToMicroseconds();

    [Fact]
    public async Task Only_messages_with_files_are_listed_newest_first_with_their_files()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "oldest", Ago(10), fileCount: 1);
        await _factory.AddMessageAsync(conversation, bob.Id, "only text", Ago(9));
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "middle", Ago(8), fileCount: 3);
        await _factory.AddMessageAsync(conversation, alice.Id, "more text", Ago(7));
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "newest", Ago(6), fileCount: 2);
        using var client = _factory.CreateClientFor(alice);

        var page = await client.GetFilesAsync(conversation);

        Assert.Equal(["newest", "middle", "oldest"], page.Select(m => m.ContentOf()));
        Assert.Equal([2, 3, 1], page.Select(m => m.AttachmentsOf().Count));
    }

    [Fact]
    public async Task A_file_comes_with_a_link_that_reads_it()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, new byte[200]);
        await client.SendMessageWithFilesAsync(conversation, null, key);

        var page = await client.GetFilesAsync(conversation);

        var file = Assert.Single(Assert.Single(page).AttachmentsOf());
        Assert.Equal(200, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await MediaApi.FollowAsync(file)).StatusCode);
    }

    [Fact]
    public async Task A_deleted_message_is_not_listed_even_though_it_had_files()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "kept", Ago(5), fileCount: 1);
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "gone", Ago(4), fileCount: 1, deleted: true);
        using var client = _factory.CreateClientFor(alice);

        var page = await client.GetFilesAsync(conversation);

        Assert.Equal(["kept"], page.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task A_conversation_without_files_has_an_empty_list()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageAsync(conversation, alice.Id, "hello", Ago(5));
        using var client = _factory.CreateClientFor(alice);

        Assert.Empty(await client.GetFilesAsync(conversation));
    }

    [Fact]
    public async Task Files_of_another_conversation_are_not_listed()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var (_, _, other) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.BackdateJoinAsync(other);
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "mine", Ago(5), fileCount: 1);
        await _factory.AddMessageWithFilesAsync(other, (await _factory.QueryDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == other).Select(m => m.UserId).FirstAsync())), "theirs", Ago(4), fileCount: 1);
        using var client = _factory.CreateClientFor(alice);

        Assert.Equal(["mine"], (await client.GetFilesAsync(conversation)).Select(m => m.ContentOf()));
    }

    // A page is 50 messages. The cursor is the id of the oldest message of the page before, and a page shorter than 50 is the last.
    [Fact]
    public async Task A_long_list_comes_in_pages_of_fifty_that_neither_skip_nor_repeat()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        const int total = GetMessageFilesHandler.PageSize + 7;
        for (var i = 0; i < total; i++)
            await _factory.AddMessageWithFilesAsync(conversation, alice.Id, $"file {i}", Ago(total - i), fileCount: 1);
        using var client = _factory.CreateClientFor(alice);

        var first = await client.GetFilesAsync(conversation);
        var second = await client.GetFilesAsync(conversation, before: first[^1].MessageIdOf());
        var third = await client.GetFilesAsync(conversation, before: second[^1].MessageIdOf());

        Assert.Equal(GetMessageFilesHandler.PageSize, first.Count);
        Assert.Equal(7, second.Count);
        Assert.Empty(third);
        Assert.Equal(
            Enumerable.Range(0, total).Reverse().Select(i => $"file {i}"),
            first.Concat(second).Select(m => m.ContentOf()));
    }

    // The place to continue from is any message the caller may read, so a client that holds a newer text message
    // (or one whose files have since been deleted) can still ask for what is older.
    [Fact]
    public async Task A_message_without_files_is_a_valid_place_to_continue_from()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "older", Ago(10), fileCount: 1);
        await _factory.AddMessageAsync(conversation, alice.Id, "text", Ago(8));
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "newer", Ago(6), fileCount: 1);
        using var client = _factory.CreateClientFor(alice);
        var text = await _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.ConversationId == conversation && !m.HasFiles).Select(m => m.Id).SingleAsync());

        var page = await client.GetFilesAsync(conversation, before: text);

        Assert.Equal(["older"], page.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task Messages_stamped_alike_are_told_apart_by_their_id_across_a_page_boundary()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        var moment = Ago(5);
        for (var i = 0; i < 3; i++)
            await _factory.AddMessageWithFilesAsync(conversation, alice.Id, $"same {i}", moment, fileCount: 1);
        using var client = _factory.CreateClientFor(alice);

        var all = await client.GetFilesAsync(conversation);
        var older = await client.GetFilesAsync(conversation, before: all[0].MessageIdOf());

        Assert.Equal(3, all.Count);
        Assert.Equal(all.Skip(1).Select(m => m.MessageIdOf()), older.Select(m => m.MessageIdOf()));
    }

    [Fact]
    public async Task Someone_who_joined_later_does_not_see_files_sent_before_they_joined()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        var group = (await aliceClient.CreateGroupAsync("Late", bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);
        var joinedAt = Ago(20);
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, joinedAt)));
        await _factory.AddMessageWithFilesAsync(group, alice.Id, "before", joinedAt.AddMinutes(-5), fileCount: 1);
        await _factory.AddMessageWithFilesAsync(group, alice.Id, "after", joinedAt.AddMinutes(5), fileCount: 1);
        using var bobClient = _factory.CreateClientFor(bob);

        Assert.Equal(["after"], (await bobClient.GetFilesAsync(group)).Select(m => m.ContentOf()));
        Assert.Equal(["after", "before"], (await aliceClient.GetFilesAsync(group)).Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task Someone_who_left_still_lists_the_files_sent_while_they_were_a_member_and_no_others()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        var group = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);
        var leftAt = Ago(30);
        await _factory.AddMessageWithFilesAsync(group, alice.Id, "while a member", leftAt.AddMinutes(-5), fileCount: 1);
        await _factory.AddMessageWithFilesAsync(group, alice.Id, "after leaving", leftAt.AddMinutes(5), fileCount: 1);
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, (DateTime?)leftAt)));
        using var bobClient = _factory.CreateClientFor(bob);

        Assert.Equal(["while a member"], (await bobClient.GetFilesAsync(group)).Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task Someone_who_is_not_a_member_is_refused_and_so_is_an_unknown_conversation()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(carol);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetFilesResponseAsync(conversation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetFilesResponseAsync(Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Asking_without_signing_in_is_refused()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetFilesResponseAsync(conversation)).StatusCode);
    }

    [Fact]
    public async Task A_cursor_that_is_unknown_from_another_conversation_or_before_the_caller_joined_gets_one_answer()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        var group = (await aliceClient.CreateGroupAsync("Cursors", bob.Id)).IdOf();
        var (_, _, other) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(group);
        await _factory.BackdateJoinAsync(other);
        var joinedAt = Ago(20);
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, joinedAt)));
        await _factory.AddMessageWithFilesAsync(group, alice.Id, "before bob", joinedAt.AddMinutes(-5), fileCount: 1);
        var beforeBob = await _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.ConversationId == group).Select(m => m.Id).SingleAsync());
        var elsewhere = (await _factory.AddMessageWithFilesAsync(
            other, await _factory.QueryDbAsync(db => db.ConversationMembers.Where(m => m.ConversationId == other).Select(m => m.UserId).FirstAsync()),
            "elsewhere", Ago(5), fileCount: 1)).MessageId;
        using var bobClient = _factory.CreateClientFor(bob);

        foreach (var cursor in new[] { Guid.NewGuid(), elsewhere, beforeBob })
        {
            var response = await bobClient.GetFilesResponseAsync(group, before: cursor);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(GetMessageHistoryHandler.UnknownCursor, (await response.ReadProblemAsync()).Detail);
        }
    }

    [Fact]
    public async Task A_before_that_is_not_an_id_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync($"/api/conversations/{conversation}/attachments?before=not-an-id");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A message that has since been deleted is still a position in the conversation, so a client that holds it can continue from it.
    [Fact]
    public async Task A_deleted_message_is_a_valid_place_to_continue_from()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "older", Ago(10), fileCount: 1);
        var deleted = (await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "gone", Ago(8), fileCount: 1, deleted: true)).MessageId;
        await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "newer", Ago(6), fileCount: 1);
        using var client = _factory.CreateClientFor(alice);

        var page = await client.GetFilesAsync(conversation, before: deleted);

        Assert.Equal(["older"], page.Select(m => m.ContentOf()));
    }

    // The upper end of the window is as much a limit for the place to continue from as the lower end: a message sent after the caller left
    // is "no such message" to them.
    [Fact]
    public async Task A_cursor_from_after_the_caller_left_is_refused()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        var group = (await aliceClient.CreateGroupAsync("Left", bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);
        var leftAt = Ago(30);
        await _factory.AddMessageWithFilesAsync(group, alice.Id, "while a member", leftAt.AddMinutes(-5), fileCount: 1);
        var after = (await _factory.AddMessageWithFilesAsync(group, alice.Id, "after leaving", leftAt.AddMinutes(5), fileCount: 1)).MessageId;
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, (DateTime?)leftAt)));
        using var bobClient = _factory.CreateClientFor(bob);

        var response = await bobClient.GetFilesResponseAsync(group, before: after);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(GetMessageHistoryHandler.UnknownCursor, (await response.ReadProblemAsync()).Detail);
    }
}
