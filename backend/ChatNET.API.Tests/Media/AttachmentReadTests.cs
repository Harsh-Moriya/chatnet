using System.Net.Http.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Media;

// Reading messages brings their files with them: history and the conversation list. The files of a whole page
// are fetched with one query, however many messages and files the page has, and a deleted message shows none.
[Collection(IntegrationTestCollection.Name)]
public class AttachmentReadTests
{
    private readonly ChatApiFactory _factory;

    public AttachmentReadTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### History #####

    [Fact]
    public async Task A_page_of_history_fetches_all_its_files_with_one_query()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        var start = DateTime.UtcNow.AddHours(-3);
        for (var i = 0; i < 50; i++)
            await _factory.AddMessageWithFilesAsync(conversation, bob.Id, $"message {i}", start.AddSeconds(i), fileCount: 3);
        var counter = new QueryCounter();
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(counter, recorder))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        counter.Reset();
        recorder.Clear();

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal(50, history.Count);
        Assert.All(history, message => Assert.Equal(3, message.AttachmentsOf().Count));
        Assert.Single(recorder.Commands, c => c.Sql.Contains("\"MessageAttachments\""));
        Assert.True(counter.Count <= 3, $"Loading history ran {counter.Count} queries; expected at most 3.");
    }

    [Fact]
    public async Task A_page_of_only_deleted_messages_asks_for_no_files_and_shows_none()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "", DateTime.UtcNow.AddHours(-1), fileCount: 2, deleted: true);
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        recorder.Clear();

        var history = await client.GetHistoryAsync(conversation);

        var message = Assert.Single(history);
        Assert.Null(message.ContentOf());
        Assert.Empty(message.AttachmentsOf());
        Assert.DoesNotContain(recorder.Commands, c => c.Sql.Contains("\"MessageAttachments\""));
    }

    [Fact]
    public async Task A_deleted_message_next_to_one_with_files_shows_files_only_on_the_one_that_has_them()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        var start = DateTime.UtcNow.AddHours(-1);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "", start, fileCount: 2, deleted: true);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "kept", start.AddSeconds(1), fileCount: 2);
        using var client = _factory.CreateClientFor(alice);

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal([0, 2], history.Select(m => m.AttachmentsOf().Count));
    }

    [Fact]
    public async Task Paging_in_either_direction_brings_the_files_too()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        var start = DateTime.UtcNow.AddHours(-1);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add((await _factory.AddMessageWithFilesAsync(conversation, bob.Id, $"m{i}", start.AddSeconds(i), fileCount: i + 1)).MessageId);
        using var client = _factory.CreateClientFor(alice);

        var older = await client.GetHistoryAsync(conversation, before: ids[2]);
        var newer = await client.GetHistoryAsync(conversation, after: ids[0]);

        Assert.Equal([1, 2], older.Select(m => m.AttachmentsOf().Count));
        Assert.Equal([2, 3], newer.Select(m => m.AttachmentsOf().Count));
    }

    [Fact]
    public async Task The_files_of_a_message_stay_in_the_order_they_were_attached_in()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "", DateTime.UtcNow.AddHours(-1), fileCount: 4);
        using var client = _factory.CreateClientFor(alice);

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal(
            ["file 0.png", "file 1.png", "file 2.png", "file 3.png"],
            Assert.Single(history).AttachmentsOf().Select(f => f.GetProperty("fileName").GetString()));
    }

    // ##### The conversation list #####

    [Fact]
    public async Task The_list_says_how_many_files_the_last_message_carries()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "", DateTime.UtcNow.AddMinutes(-1), fileCount: 3);
        using var client = _factory.CreateClientFor(alice);

        var last = LastMessageOf(await client.ListConversationsAsync(), conversation);

        Assert.Equal(3, last.GetProperty("attachmentCount").GetInt32());
        Assert.Equal("", last.GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_list_shows_the_text_and_the_count_when_a_message_has_both()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "look at this", DateTime.UtcNow.AddMinutes(-1), fileCount: 1);
        using var client = _factory.CreateClientFor(alice);

        var last = LastMessageOf(await client.ListConversationsAsync(), conversation);

        Assert.Equal("look at this", last.GetProperty("content").GetString());
        Assert.Equal(1, last.GetProperty("attachmentCount").GetInt32());
    }

    [Fact]
    public async Task Only_the_newest_message_counts_for_the_list()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        var start = DateTime.UtcNow.AddHours(-1);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "", start, fileCount: 4);
        await _factory.AddMessageAsync(conversation, bob.Id, "just words", start.AddMinutes(1));
        using var client = _factory.CreateClientFor(alice);

        var last = LastMessageOf(await client.ListConversationsAsync(), conversation);

        Assert.Equal("just words", last.GetProperty("content").GetString());
        Assert.Equal(0, last.GetProperty("attachmentCount").GetInt32());
    }

    [Fact]
    public async Task A_deleted_last_message_counts_no_files()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "", DateTime.UtcNow.AddMinutes(-1), fileCount: 2, deleted: true);
        using var client = _factory.CreateClientFor(alice);

        var last = LastMessageOf(await client.ListConversationsAsync(), conversation);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, last.GetProperty("content").ValueKind);
        Assert.Equal(0, last.GetProperty("attachmentCount").GetInt32());
    }

    [Fact]
    public async Task One_conversation_asked_for_alone_says_the_same()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessageWithFilesAsync(conversation, bob.Id, "", DateTime.UtcNow.AddMinutes(-1), fileCount: 2);
        using var client = _factory.CreateClientFor(alice);

        var single = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/conversations/{conversation}");

        Assert.Equal(2, single.GetProperty("lastMessage").GetProperty("attachmentCount").GetInt32());
    }

    private static System.Text.Json.JsonElement LastMessageOf(List<System.Text.Json.JsonElement> list, Guid conversation) =>
        list.Single(c => c.IdOf() == conversation).GetProperty("lastMessage");
}
