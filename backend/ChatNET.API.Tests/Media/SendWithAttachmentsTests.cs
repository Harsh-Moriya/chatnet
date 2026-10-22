using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Media;

// A message can carry files. They are uploaded first, straight to the bucket, and the message only names them.
// Sending checks what was really stored, moves each file to its permanent place and saves the message together
// with its files, so a message is never stored with a file that is not there and nothing is left behind when a
// send fails.
[Collection(IntegrationTestCollection.Name)]
public class SendWithAttachmentsTests
{
    private const string NotUploaded = "One of the files was not uploaded.";

    private readonly ChatApiFactory _factory;

    public SendWithAttachmentsTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private Task<List<MessageAttachment>> StoredAttachmentsAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.MessageAttachments
            .Where(a => a.Message.ConversationId == conversation)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync());

    private Task<int> MessageCountAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Messages.CountAsync(m => m.ConversationId == conversation));

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    private WebApplicationFactory<Program> WithSetting(string key, string value) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

    // ##### What is stored and returned #####

    [Fact]
    public async Task A_message_of_only_files_is_stored_with_empty_text_and_its_files()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var content = Bytes(300);
        var key = await client.UploadFileAsync(conversation, content);

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "photo.png")]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("", message.ContentOf());
        var file = Assert.Single(message.AttachmentsOf());
        Assert.Equal("Image", file.GetProperty("type").GetString());
        Assert.Equal("photo.png", file.GetProperty("fileName").GetString());
        Assert.Equal("image/png", file.GetProperty("contentType").GetString());
        Assert.Equal(300, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(content, await (await MediaApi.FollowAsync(file)).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Text_and_files_are_sent_together_and_the_text_is_trimmed()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));

        var message = await client.SendMessageWithFilesAsync(conversation, "  look at this  ", (key, "a.png"));

        Assert.Equal("look at this", message.ContentOf());
        Assert.Single(message.AttachmentsOf());
    }

    [Fact]
    public async Task The_file_is_moved_to_its_permanent_place_and_the_upload_is_removed()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var content = Bytes(64);
        var key = await client.UploadFileAsync(conversation, content);

        await client.SendMessageWithFilesAsync(conversation, null, (key, "a.png"));

        var stored = Assert.Single(await StoredAttachmentsAsync(conversation));
        Assert.StartsWith($"attachments/{conversation}/{stored.Id}", stored.StorageKey);
        Assert.Equal(content, await _factory.GetObjectBytesAsync(stored.StorageKey));
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task Files_keep_the_order_they_were_attached_in()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var one = await client.UploadFileAsync(conversation, Bytes(10), "image/png", "one.png");
        var two = await client.UploadFileAsync(conversation, Bytes(20), "application/pdf", "two.pdf");
        var three = await client.UploadFileAsync(conversation, Bytes(30), "image/png", "three.png");
        var four = await client.UploadFileAsync(conversation, Bytes(40), "image/png", "four.png");

        var message = await client.SendMessageWithFilesAsync(
            conversation, null, (three, "three.png"), (one, "one.png"), (four, "four.png"), (two, "two.pdf"));

        var names = new[] { "three.png", "one.png", "four.png", "two.pdf" };
        Assert.Equal(names, message.AttachmentsOf().Select(f => f.GetProperty("fileName").GetString()));
        var history = await client.GetHistoryAsync(conversation);
        Assert.Equal(names, history[^1].AttachmentsOf().Select(f => f.GetProperty("fileName").GetString()));
    }

    // The order is not in the ids: .NET's version 7 GUIDs made within one millisecond come out in a different order
    // than they were made in, nine times in ten. Only the stamps that run one microsecond apart keep it. With a real
    // storage each file takes a round trip and the ids are made milliseconds apart, so this uses a storage that
    // answers at once. By chance alone one send in 24 would keep the order, so it is checked several times.
    [Fact]
    public async Task Files_looked_at_within_one_millisecond_still_come_back_in_the_order_they_were_attached_in()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage, InstantBlobStorage>();
        }));
        using var client = Authorized(host, alice);

        for (var round = 0; round < 8; round++)
        {
            var names = Enumerable.Range(0, 4).Select(i => $"file {round}-{i}.png").ToArray();
            var files = names.Select(name => (AttachmentKeys.NewUploadKey(conversation, alice.Id), name)).ToArray();

            await client.SendMessageWithFilesAsync(conversation, null, files);

            var history = await client.GetHistoryAsync(conversation);
            Assert.Equal(names, history[^1].AttachmentsOf().Select(f => f.GetProperty("fileName").GetString()));
        }
    }

    // Pictures are shown in the page; any other file is a download. Checked on the links of the answer to the send,
    // of the push and of history, which are made in one place.
    [Fact]
    public async Task The_links_in_a_message_show_a_picture_and_download_any_other_file()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        var picture = await client.UploadFileAsync(conversation, Bytes(10), "image/png", "pic.png");
        var document = await client.UploadFileAsync(conversation, Bytes(10), "application/pdf", "doc.pdf");

        var sent = await client.SendMessageWithFilesAsync(conversation, null, (picture, "pic.png"), (document, "doc.pdf"));

        var pushed = await bobsTab.WaitForMessageAsync(conversation, sent.MessageIdOf());
        var fromHistory = (await client.GetHistoryAsync(conversation))[^1];
        foreach (var message in new[] { sent, pushed, fromHistory })
        {
            var files = message.AttachmentsOf();
            Assert.Equal("inline", (await MediaApi.FollowAsync(files[0])).Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("attachment", (await MediaApi.FollowAsync(files[1])).Content.Headers.ContentDisposition?.DispositionType);
        }
    }

    [Fact]
    public async Task The_kind_of_a_file_follows_the_type_it_was_stored_with()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var image = await client.UploadFileAsync(conversation, Bytes(10), "image/webp", "a.webp");
        var video = await client.UploadFileAsync(conversation, Bytes(10), "video/mp4", "b.mp4");
        var document = await client.UploadFileAsync(conversation, Bytes(10), "application/pdf", "c.pdf");

        var message = await client.SendMessageWithFilesAsync(
            conversation, null, (image, "a.webp"), (video, "b.mp4"), (document, "c.pdf"));

        Assert.Equal(["Image", "Video", "File"], message.AttachmentsOf().Select(f => f.GetProperty("type").GetString()));
    }

    // What the sender claimed is not what is trusted: the object in the bucket is looked at.
    [Fact]
    public async Task The_type_and_size_come_from_the_stored_object_and_not_from_the_ticket()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation, "photo.png", "image/png", 5);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        await _factory.PutObjectAsync(key, Bytes(777), "application/pdf");

        var message = await client.SendMessageWithFilesAsync(conversation, null, (key, "report.pdf"));

        var file = Assert.Single(message.AttachmentsOf());
        Assert.Equal("application/pdf", file.GetProperty("contentType").GetString());
        Assert.Equal("File", file.GetProperty("type").GetString());
        Assert.Equal(777, file.GetProperty("sizeBytes").GetInt64());
    }

    [Theory]
    [InlineData("C:\\Users\\me\\docs\\plan.pdf", "plan.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("invoice\u202Egpj.exe", "invoicegpj.exe")]
    [InlineData("two\r\nlines.txt", "twolines.txt")]
    public async Task A_file_name_is_made_harmless_before_it_is_stored(string sent, string stored)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));

        var message = await client.SendMessageWithFilesAsync(conversation, null, (key, sent));

        Assert.Equal(stored, Assert.Single(message.AttachmentsOf()).GetProperty("fileName").GetString());
        Assert.Equal(stored, Assert.Single(await StoredAttachmentsAsync(conversation)).FileName);
    }

    // ##### Reading and pushing #####

    [Fact]
    public async Task History_returns_the_files_of_a_message_with_links_that_download_them()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var content = Bytes(120);
        var key = await bobClient.UploadFileAsync(conversation, content);
        await bobClient.SendMessageWithFilesAsync(conversation, "from bob", (key, "pic.png"));

        var history = await aliceClient.GetHistoryAsync(conversation);

        var file = Assert.Single(Assert.Single(history).AttachmentsOf());
        Assert.Equal("pic.png", file.GetProperty("fileName").GetString());
        Assert.Equal(content, await (await MediaApi.FollowAsync(file)).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_message_without_files_has_an_empty_list()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var message = await client.SendMessageAsync(conversation, "just text");

        Assert.Empty(message.AttachmentsOf());
        Assert.Empty(Assert.Single(await client.GetHistoryAsync(conversation)).AttachmentsOf());
    }

    [Fact]
    public async Task The_push_to_the_conversation_carries_the_files_with_links_that_work()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        var content = Bytes(80);
        var key = await client.UploadFileAsync(conversation, content);

        var sent = await client.SendMessageWithFilesAsync(conversation, null, (key, "pic.png"));

        var pushed = await bobsTab.WaitForMessageAsync(conversation, sent.MessageIdOf());
        var file = Assert.Single(pushed.AttachmentsOf());
        Assert.Equal(content, await (await MediaApi.FollowAsync(file)).Content.ReadAsByteArrayAsync());
    }

    // ##### Refusals #####

    [Fact]
    public async Task More_files_than_the_limit_are_refused_and_nothing_is_stored()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var files = new List<(string, string)>();
        for (var i = 0; i < 5; i++)
            files.Add((await client.UploadFileAsync(conversation, Bytes(10)), $"{i}.png"));

        var response = await client.PostMessageWithFilesAsync(conversation, null, files);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync()).Errors!.Value.TryGetProperty("Attachments", out _));
        Assert.Equal(0, await MessageCountAsync(conversation));
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
    }

    [Fact]
    public async Task The_same_upload_named_twice_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png"), (key, "b.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    [Theory]
    [InlineData(null, "a.png")]
    [InlineData("", "a.png")]
    [InlineData("pending/x", null)]
    [InlineData("pending/x", "")]
    public async Task A_file_without_a_key_or_a_name_is_refused(string? key, string? name)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { attachments = new[] { new { uploadKey = key, fileName = name } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Text_that_is_too_long_is_still_refused_when_files_are_attached()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));

        var response = await client.PostMessageWithFilesAsync(conversation, new string('x', 4001), [(key, "a.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_message_with_neither_text_nor_files_is_still_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var empty = await client.PostMessageWithFilesAsync(conversation, "   ", []);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    // Someone else's key must say nothing about their files: it is refused exactly as a key that points at
    // nothing is, and their file is left alone.
    [Fact]
    public async Task Another_persons_upload_cannot_be_attached()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var bobsKey = await bobClient.UploadFileAsync(conversation, Bytes(10));

        var response = await aliceClient.PostMessageWithFilesAsync(conversation, null, [(bobsKey, "stolen.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(NotUploaded, (await response.ReadProblemAsync()).Detail);
        Assert.True(await _factory.ObjectExistsAsync(bobsKey));
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    [Fact]
    public async Task An_upload_made_for_another_conversation_cannot_be_attached()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var other = (await client.CreateGroupAsync("Elsewhere", bob.Id)).IdOf();
        var key = await client.UploadFileAsync(other, Bytes(10));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task A_key_for_an_upload_that_never_happened_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation);

        var response = await client.PostMessageWithFilesAsync(
            conversation, null, [(ticket.GetProperty("uploadKey").GetString()!, "a.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(NotUploaded, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not a key at all")]
    [InlineData("pending/../../attachments/anything")]
    [InlineData("attachments/00000000-0000-0000-0000-000000000000/00000000-0000-0000-0000-000000000000")]
    public async Task A_key_the_server_never_issued_is_refused(string key)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(NotUploaded, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_file_that_is_already_attached_cannot_be_attached_again_by_its_stored_key()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));
        await client.SendMessageWithFilesAsync(conversation, null, (key, "a.png"));
        var stored = Assert.Single(await StoredAttachmentsAsync(conversation));

        var again = await client.PostMessageWithFilesAsync(conversation, null, [(stored.StorageKey, "again.png")]);
        var sameUpload = await client.PostMessageWithFilesAsync(conversation, null, [(key, "again.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sameUpload.StatusCode);
        Assert.Single(await StoredAttachmentsAsync(conversation));
    }

    // The ticket promised a size, but a presigned PUT cannot hold the sender to it. The object is looked at.
    [Fact]
    public async Task An_upload_larger_than_the_limit_is_refused_and_deleted()
    {
        using var host = WithSetting("Storage:MaxFileBytes", "1024");
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = Authorized(host, alice);
        var ticket = await client.GetUploadTicketAsync(conversation, "big.png", "image/png", 100);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        (await MediaApi.PutToTicketAsync(ticket, Bytes(5000), "image/png")).EnsureSuccessStatusCode();

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "big.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("larger than the limit", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.ObjectExistsAsync(key));
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    [Fact]
    public async Task An_empty_upload_is_refused_and_deleted()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        (await MediaApi.PutToTicketAsync(ticket, [], "image/png")).EnsureSuccessStatusCode();

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "empty.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("empty", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task An_upload_of_a_type_that_is_not_allowed_is_refused_and_deleted()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        await _factory.PutObjectAsync(key, Bytes(10), "text/html");

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "page.html")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("type that cannot be sent", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task When_a_later_file_is_refused_the_copies_already_made_are_taken_back()
    {
        using var host = WithSetting("Storage:MaxFileBytes", "1024");
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = Authorized(host, alice);
        var good = await client.UploadFileAsync(conversation, Bytes(100), "image/png", "good.png");
        var bigTicket = await client.GetUploadTicketAsync(conversation, "big.png", "image/png", 10);
        var big = bigTicket.GetProperty("uploadKey").GetString()!;
        await _factory.PutObjectAsync(big, Bytes(5000));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(good, "good.png"), (big, "big.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
        Assert.True(await _factory.ObjectExistsAsync(good));
        Assert.False(await _factory.ObjectExistsAsync(big));
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    // The gap between looking at an upload and copying it: the object is replaced in between.
    [Fact]
    public async Task An_upload_replaced_after_it_was_looked_at_is_not_copied()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(provider => new ReplacingBlobStorage(
                ActivatorUtilities.CreateInstance<S3BlobStorage>(provider),
                key => _factory.PutObjectAsync(key, Bytes(900), "image/png")));
        }));
        using var client = Authorized(host, alice);
        var key = await client.UploadFileAsync(conversation, Bytes(100));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(NotUploaded, await response.Content.ReadAsStringAsync());
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    [Fact]
    public async Task Someone_who_has_left_cannot_send_files_and_their_upload_is_left_alone()
    {
        var (_, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(bob);
        var key = await client.UploadFileAsync(conversation, Bytes(10));
        await _factory.ExecuteDbAsync(async db =>
        {
            db.ConversationMembers.Single(m => m.ConversationId == conversation && m.UserId == bob.Id).LeftAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await _factory.ObjectExistsAsync(key));
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
    }

    [Fact]
    public async Task A_deactivated_account_cannot_send_files()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));
        await _factory.DeactivateAsync(alice);

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
    }

    // ##### A failed save #####

    [Fact]
    public async Task When_the_message_cannot_be_saved_the_copies_are_taken_back_and_the_upload_stays_for_a_retry()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessageRepository>();
            services.AddScoped<IMessageRepository>(provider =>
                new ThrowingMessageRepository(ActivatorUtilities.CreateInstance<MessageRepository>(provider)));
        }));
        using var client = Authorized(host, alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")]);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
        Assert.True(await _factory.ObjectExistsAsync(key));
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    // ##### The same send again #####

    [Fact]
    public async Task A_retry_returns_the_stored_message_with_its_files_and_attaches_nothing_new()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));
        var clientMessageId = Guid.NewGuid();
        var first = await client.PostMessageWithFilesAsync(conversation, "once", [(key, "a.png")], clientMessageId);
        var sent = await first.Content.ReadFromJsonAsync<JsonElement>();

        var retry = await client.PostMessageWithFilesAsync(conversation, "once", [(key, "a.png")], clientMessageId);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var again = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sent.MessageIdOf(), again.MessageIdOf());
        Assert.Equal(Assert.Single(sent.AttachmentsOf()).IdOf(), Assert.Single(again.AttachmentsOf()).IdOf());
        Assert.Single(await StoredAttachmentsAsync(conversation));
        Assert.Single(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
        Assert.Equal(HttpStatusCode.OK, (await MediaApi.FollowAsync(Assert.Single(again.AttachmentsOf()))).StatusCode);
    }

    // The other copy of the send attached the files and removed the uploads while this one was looking for them.
    [Fact]
    public async Task A_copy_that_finds_the_uploads_already_used_answers_with_the_message_that_used_them()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));
        var clientMessageId = Guid.NewGuid();
        var sent = await (await client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")], clientMessageId))
            .Content.ReadFromJsonAsync<JsonElement>();
        using var host = HostWhoseFirstLookupFindsNothing();
        using var late = Authorized(host, alice);

        var response = await late.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")], clientMessageId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(sent.MessageIdOf(), (await response.Content.ReadFromJsonAsync<JsonElement>()).MessageIdOf());
        Assert.Single(await StoredAttachmentsAsync(conversation));
    }

    // The copy got as far as copying the files, then the index told it another copy was stored first.
    [Fact]
    public async Task A_copy_that_loses_to_the_index_takes_back_the_files_it_made()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));
        var clientMessageId = Guid.NewGuid();
        await _factory.AddMessageAsync(conversation, alice.Id, "winner", DateTime.UtcNow.AddMinutes(-1), clientMessageId);
        using var host = HostWhoseFirstLookupFindsNothing();
        using var loser = Authorized(host, alice);

        var response = await loser.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")], clientMessageId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("winner", (await response.Content.ReadFromJsonAsync<JsonElement>()).ContentOf());
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
        Assert.Empty(await StoredAttachmentsAsync(conversation));
    }

    [Fact]
    public async Task Many_copies_of_one_send_with_files_store_one_message_and_one_set_of_files()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10));
        var clientMessageId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.PostMessageWithFilesAsync(conversation, null, [(key, "a.png")], clientMessageId)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).MessageIdOf());
        Assert.Single(ids);
        Assert.Equal(1, await MessageCountAsync(conversation));
        Assert.Single(await StoredAttachmentsAsync(conversation));
        Assert.Single(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
    }

    // ##### Logging #####

    [Fact]
    public async Task The_log_has_ids_and_a_count_and_never_the_file_name_or_the_text()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);
        var key = await client.UploadFileAsync(conversation, Bytes(10), "image/png", "private holiday photo.png");

        var message = await client.SendMessageWithFilesAsync(conversation, "my secret caption", (key, "private holiday photo.png"));

        Assert.Contains(capture.Entries, e => e.Contains(message.MessageIdOf().ToString()) && e.Contains("with 1 attachments"));
        Assert.DoesNotContain(capture.Entries, e => e.Contains("holiday") || e.Contains("secret caption"));
    }

    // A send whose first lookup finds nothing, whatever is stored, so that it goes on to look for the files and
    // to insert, as a second copy does when it arrives in the moment before the first is saved.
    private WebApplicationFactory<Program> HostWhoseFirstLookupFindsNothing() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessageRepository>();
            services.AddScoped<IMessageRepository>(provider =>
                new ForgetfulMessageRepository(ActivatorUtilities.CreateInstance<MessageRepository>(provider)));
        }));
}
