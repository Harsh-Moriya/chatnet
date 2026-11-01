using System.Net;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Messaging;

// A search result is opened in its place: the history around it, a page just older, the message, and a page just newer, so
// the reader can scroll either way from it. The cursor rules are the ones of the other two: the message must be one the
// caller may read, and anything else is one answer.
[Collection(IntegrationTestCollection.Name)]
public class MessageHistoryAroundTests
{
    private readonly ChatApiFactory _factory;

    public MessageHistoryAroundTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // "m 0" is the oldest. Returns the id of each message by number.
    private async Task<(TestUser Alice, TestUser Bob, Guid Conversation, Dictionary<int, Guid> Ids)> ConversationOfAsync(int count)
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessagesAsync(conversation, bob.Id, count, DateTime.UtcNow.AddHours(-5), TimeSpan.FromSeconds(1), prefix: "m");

        var ids = await _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.ConversationId == conversation)
            .ToDictionaryAsync(m => int.Parse(m.Content!.Split(' ')[1]), m => m.Id));

        return (alice, bob, conversation, ids);
    }

    private static List<int> Numbers(IEnumerable<JsonElement> messages) =>
        messages.Select(m => int.Parse(m.ContentOf()!.Split(' ')[1])).ToList();

    [Fact]
    public async Task A_message_in_the_middle_comes_with_a_page_on_each_side_oldest_first()
    {
        var (alice, _, conversation, ids) = await ConversationOfAsync(200);
        using var client = _factory.CreateClientFor(alice);

        var around = await client.GetHistoryAsync(conversation, around: ids[100]);

        Assert.Equal(Enumerable.Range(50, 101), Numbers(around));
    }

    [Theory]
    [InlineData(5, 0, 55)]
    [InlineData(0, 0, 50)]
    [InlineData(199, 149, 199)]
    [InlineData(194, 144, 199)]
    [InlineData(50, 0, 100)]
    [InlineData(149, 99, 199)]
    public async Task Near_either_end_the_page_on_that_side_is_shorter(int target, int first, int last)
    {
        var (alice, _, conversation, ids) = await ConversationOfAsync(200);
        using var client = _factory.CreateClientFor(alice);

        var around = await client.GetHistoryAsync(conversation, around: ids[target]);

        Assert.Equal(Enumerable.Range(first, last - first + 1), Numbers(around));
    }

    [Fact]
    public async Task A_short_conversation_comes_back_whole()
    {
        var (alice, _, conversation, ids) = await ConversationOfAsync(3);
        using var client = _factory.CreateClientFor(alice);

        var around = await client.GetHistoryAsync(conversation, around: ids[1]);

        Assert.Equal([0, 1, 2], Numbers(around));
    }

    [Fact]
    public async Task The_pages_continue_with_the_other_two_cursors_with_nothing_missing_or_repeated()
    {
        var (alice, _, conversation, ids) = await ConversationOfAsync(260);
        using var client = _factory.CreateClientFor(alice);

        var around = await client.GetHistoryAsync(conversation, around: ids[130]);
        var older = await client.GetHistoryAsync(conversation, before: around[0].MessageIdOf());
        var newer = await client.GetHistoryAsync(conversation, after: around[^1].MessageIdOf());

        Assert.Equal(Enumerable.Range(30, 50), Numbers(older));
        Assert.Equal(Enumerable.Range(181, 50), Numbers(newer));
    }

    [Fact]
    public async Task A_deleted_message_is_returned_deleted_in_its_place()
    {
        var (alice, bob, conversation, ids) = await ConversationOfAsync(10);
        using var bobClient = _factory.CreateClientFor(bob);
        using var client = _factory.CreateClientFor(alice);
        await bobClient.DeleteMessageAsync(conversation, ids[4]);

        var around = await client.GetHistoryAsync(conversation, around: ids[4]);

        var deleted = around.Single(m => m.MessageIdOf() == ids[4]);
        Assert.True(deleted.GetProperty("isDeleted").GetBoolean());
        Assert.Null(deleted.ContentOf());
        Assert.Equal(10, around.Count);
    }

    [Fact]
    public async Task The_files_of_the_messages_come_with_them()
    {
        var (alice, bob, conversation, _) = await ConversationOfAsync(4);
        using var client = _factory.CreateClientFor(alice);
        var (withFile, _) = await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "see file", DateTime.UtcNow.AddHours(-1), 2);

        var around = await client.GetHistoryAsync(conversation, around: withFile);

        Assert.Equal(2, around.Single(m => m.MessageIdOf() == withFile).AttachmentsOf().Count);
    }

    // ##### Who may ask for what #####

    [Fact]
    public async Task A_message_that_is_not_one_the_caller_may_read_is_the_same_answer_as_one_that_does_not_exist()
    {
        var (alice, _, conversation, ids) = await ConversationOfAsync(5);
        var (_, _, otherConversation, otherIds) = await ConversationOfAsync(5);
        using var client = _factory.CreateClientFor(alice);
        // Sent a day before the join time the conversation was given.
        await _factory.AddMessageAsync(conversation, alice.Id, "m 99", DateTime.UtcNow.AddHours(-30));
        var beforeJoining = await _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.ConversationId == conversation && m.Content == "m 99").Select(m => m.Id).SingleAsync());

        var unknown = await client.GetHistoryResponseAsync(conversation, around: Guid.NewGuid());
        var foreign = await client.GetHistoryResponseAsync(conversation, around: otherIds[2]);
        var early = await client.GetHistoryResponseAsync(conversation, around: beforeJoining);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        var body = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("\"Around\"", body);
        Assert.Contains("Unknown message cursor.", body);
        Assert.Equal(body.Split("traceId")[0], (await foreign.Content.ReadAsStringAsync()).Split("traceId")[0]);
        Assert.Equal(body.Split("traceId")[0], (await early.Content.ReadAsStringAsync()).Split("traceId")[0]);
        Assert.Equal(5, (await client.GetHistoryAsync(conversation, around: ids[2])).Count);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Only_one_cursor_may_be_given(string other)
    {
        var (alice, _, conversation, ids) = await ConversationOfAsync(5);
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync($"/api/conversations/{conversation}/messages?around={ids[2]}&{other}={ids[3]}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Use only one of", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_stranger_is_refused_as_for_every_page()
    {
        var (_, _, conversation, ids) = await ConversationOfAsync(5);
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = _factory.CreateClientFor(stranger);

        var response = await client.GetHistoryResponseAsync(conversation, around: ids[2]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Someone_who_left_gets_the_window_they_had_and_nothing_after_it()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var leaver = await _factory.RegisterUserAsync("leaver");
        using var ownerClient = _factory.CreateClientFor(owner);
        using var leaverClient = _factory.CreateClientFor(leaver);
        var group = (await ownerClient.CreateGroupAsync("Team", leaver.Id)).IdOf();
        var inside = (await ownerClient.SendMessageAsync(group, "inside 1")).MessageIdOf();
        await ownerClient.SendMessageAsync(group, "inside 2");
        (await leaverClient.LeaveResponseAsync(group)).EnsureSuccessStatusCode();
        var after = (await ownerClient.SendMessageAsync(group, "after")).MessageIdOf();

        var around = await leaverClient.GetHistoryAsync(group, around: inside);
        var outside = await leaverClient.GetHistoryResponseAsync(group, around: after);

        Assert.Equal(["inside 1", "inside 2"], around.Select(m => m.ContentOf()));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
    }
}
