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

// A message can carry files. They are encrypted by the sender's browser and uploaded first, straight to the bucket, and the message
// only names them. Sending checks what was really stored (its size: the server cannot read it), moves each file to its permanent
// place and saves the message together with its files, so a message is never stored with a file that is not there and nothing is
// left behind when a send fails.
[Collection(IntegrationTestCollection.Name)]
public class SendWithAttachmentsTests
{
    private const string NotUploaded = "One of the files was not uploaded.";

    private readonly ChatApiFactory _factory;

    public SendWithAttachmentsTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // What a browser would have uploaded for a file: bytes of this length that mean nothing here. At least SealedFile.MinBytes.
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
    public async Task A_message_of_only_files_is_stored_with_a_blob_and_its_files()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var content = Bytes(300);
        var key = await client.UploadFileAsync(conversation, content);

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("", message.ContentOf());
        var file = Assert.Single(message.AttachmentsOf());
        Assert.Equal(300, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(content, await (await MediaApi.FollowAsync(file)).Content.ReadAsByteArrayAsync());
    }

    // The server cannot say what a file is, so a file says nothing but its id, its size and where to get it.
    [Fact]
    public async Task A_file_is_returned_as_an_id_a_size_and_a_link_and_nothing_else()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var message = await client.SendMessageWithFilesAsync(conversation, null, key);

        var file = Assert.Single(message.AttachmentsOf());
        Assert.Equal(["id", "sizeBytes", "url"], file.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Text_and_files_are_sent_together()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var message = await client.SendMessageWithFilesAsync(conversation, "look at this", key);

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

        await client.SendMessageWithFilesAsync(conversation, null, key);

        var stored = Assert.Single(await StoredAttachmentsAsync(conversation));
        Assert.StartsWith($"attachments/{conversation}/{stored.Id}", stored.StorageKey);
        Assert.Equal(content, await _factory.GetObjectBytesAsync(stored.StorageKey));
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    // The bucket is told nothing about the file: the upload is signed for one content type, whatever the bytes are.
    [Fact]
    public async Task The_object_in_the_bucket_has_a_content_type_that_says_nothing_about_the_file()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        await client.SendMessageWithFilesAsync(conversation, null, key);

        var stored = Assert.Single(await StoredAttachmentsAsync(conversation));
        var metadata = await _factory.Storage.GetObjectMetadataAsync(ChatApiFactory.StorageBucket, stored.StorageKey);
        Assert.Equal("application/octet-stream", metadata.Headers.ContentType);
    }

    // The files of a message are told apart by their sizes here, which the tests choose to differ.
    [Fact]
    public async Task Files_keep_the_order_they_were_attached_in()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var one = await client.UploadFileAsync(conversation, Bytes(100));
        var two = await client.UploadFileAsync(conversation, Bytes(200));
        var three = await client.UploadFileAsync(conversation, Bytes(300));
        var four = await client.UploadFileAsync(conversation, Bytes(400));

        var message = await client.SendMessageWithFilesAsync(conversation, null, three, one, four, two);

        long[] sizes = [300, 100, 400, 200];
        Assert.Equal(sizes, message.AttachmentsOf().Select(f => f.GetProperty("sizeBytes").GetInt64()));
        var history = await client.GetHistoryAsync(conversation);
        Assert.Equal(sizes, history[^1].AttachmentsOf().Select(f => f.GetProperty("sizeBytes").GetInt64()));
    }

    // The order is not in the ids: .NET's version 7 GUIDs made within one millisecond come out in a different order
    // than they were made in, nine times in ten. Only the stamps that run one microsecond apart keep it. With a real
    // storage each file takes a round trip and the ids are made milliseconds apart, so this uses a storage that
    // answers at once. By chance alone one send in 24 would keep the order, so it is checked several times.
    [Fact]
    public async Task Files_looked_at_within_one_millisecond_still_come_back_in_the_order_they_were_attached_in()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var storage = new InstantBlobStorage();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(storage);
        }));
        using var client = Authorized(host, alice);

        for (var round = 0; round < 8; round++)
        {
            var keys = Enumerable.Range(0, 4).Select(_ => AttachmentKeys.NewUploadKey(conversation, alice.Id)).ToArray();

            await client.SendMessageWithFilesAsync(conversation, null, keys);

            var expected = keys.Select(storage.LinkOf).ToArray();
            var history = await client.GetHistoryAsync(conversation);
            Assert.Equal(expected, history[^1].AttachmentsOf().Select(f => f.UrlOf()));
        }
    }

    // Checked on the links of the answer to the send, of the push and of history, which are made in one place.
    [Fact]
    public async Task The_links_in_a_message_download_the_bytes_that_were_stored()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        var content = Bytes(500);
        var key = await client.UploadFileAsync(conversation, content);

        var sent = await client.SendMessageWithFilesAsync(conversation, null, key);

        var pushed = await bobsTab.WaitForMessageAsync(conversation, sent.MessageIdOf());
        var fromHistory = (await client.GetHistoryAsync(conversation))[^1];
        foreach (var message in new[] { sent, pushed, fromHistory })
        {
            var response = await MediaApi.FollowAsync(Assert.Single(message.AttachmentsOf()));
            Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
            Assert.Null(response.Content.Headers.ContentDisposition);
        }
    }

    // The size is what the bucket holds, whatever the ticket promised.
    [Fact]
    public async Task The_size_comes_from_the_stored_object_and_not_from_the_ticket()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation, SealedFile.MinBytes);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        await _factory.PutObjectAsync(key, Bytes(777));

        var message = await client.SendMessageWithFilesAsync(conversation, null, key);

        Assert.Equal(777, Assert.Single(message.AttachmentsOf()).GetProperty("sizeBytes").GetInt64());
    }

    // The server does not look at what a file is, so whatever the bucket was told about it is not a reason to refuse it.
    [Fact]
    public async Task A_file_is_not_refused_for_what_the_bucket_says_it_is()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation, 64);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        await _factory.PutObjectAsync(key, Bytes(64), "text/html");

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
        await bobClient.SendMessageWithFilesAsync(conversation, "from bob", key);

        var history = await aliceClient.GetHistoryAsync(conversation);

        var file = Assert.Single(Assert.Single(history).AttachmentsOf());
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

        var sent = await client.SendMessageWithFilesAsync(conversation, null, key);

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
        var keys = new List<string>();
        for (var i = 0; i < 5; i++)
            keys.Add(await client.UploadFileAsync(conversation, Bytes(64)));

        var response = await client.PostMessageWithFilesAsync(conversation, null, keys);

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
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key, key]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_file_without_a_key_is_refused(string? key)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { attachments = new[] { new { uploadKey = key } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A name sent with a file is not read: the name of a file is inside the sealed message, and a client that still sends one
    // is not refused for it.
    [Fact]
    public async Task A_name_sent_with_a_file_is_ignored_and_never_stored()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new
        {
            ciphertext = FakeMessage.Ciphertext(await client.CurrentEpochAsync(conversation), string.Empty),
            clientMessageId = Guid.NewGuid(),
            attachments = new[] { new { uploadKey = key, fileName = "holiday photo.png" } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("holiday", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_sealed_text_that_is_too_long_is_still_refused_when_files_are_attached()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var response = await client.PostMessageWithFilesAsync(conversation, new string('x', 32768), [key]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_message_with_files_but_nothing_sealed_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new
        {
            clientMessageId = Guid.NewGuid(),
            attachments = new[] { new { uploadKey = key } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Someone else's key must say nothing about their files: it is refused exactly as a key that points at
    // nothing is, and their file is left alone.
    [Fact]
    public async Task Another_persons_upload_cannot_be_attached()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var bobsKey = await bobClient.UploadFileAsync(conversation, Bytes(64));

        var response = await aliceClient.PostMessageWithFilesAsync(conversation, null, [bobsKey]);

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
        var key = await client.UploadFileAsync(other, Bytes(64));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

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
            conversation, null, [ticket.GetProperty("uploadKey").GetString()!]);

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

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(NotUploaded, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_file_that_is_already_attached_cannot_be_attached_again_by_its_stored_key()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));
        await client.SendMessageWithFilesAsync(conversation, null, key);
        var stored = Assert.Single(await StoredAttachmentsAsync(conversation));

        var again = await client.PostMessageWithFilesAsync(conversation, null, [stored.StorageKey]);
        var sameUpload = await client.PostMessageWithFilesAsync(conversation, null, [key]);

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
        var ticket = await client.GetUploadTicketAsync(conversation, 100);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        (await MediaApi.PutToTicketAsync(ticket, Bytes(5000))).EnsureSuccessStatusCode();

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("larger than the limit", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.ObjectExistsAsync(key));
        Assert.Equal(0, await MessageCountAsync(conversation));
    }

    // The limit is on the file as a person sees it, and the largest object is that file with its header and a tag for every chunk of
    // 64 KiB: 1024 bytes become 1024 + 13 + 16 = 1053. One byte more is refused.
    [Fact]
    public async Task The_largest_object_for_a_file_of_the_limit_is_accepted_and_one_byte_more_is_refused()
    {
        using var host = WithSetting("Storage:MaxFileBytes", "1024");
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = Authorized(host, alice);
        var largest = await client.UploadFileAsync(conversation, Bytes(1053));
        var tooLarge = await client.UploadFileAsync(conversation, Bytes(1053));
        await _factory.PutObjectAsync(tooLarge, Bytes(1054));

        var accepted = await client.PostMessageWithFilesAsync(conversation, null, [largest]);
        var refused = await client.PostMessageWithFilesAsync(conversation, null, [tooLarge]);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(await _factory.ObjectExistsAsync(tooLarge));
    }

    // Nothing smaller than a header and the tag of an empty file is a sealed file.
    [Fact]
    public async Task An_upload_smaller_than_any_sealed_file_is_refused_and_deleted()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation);
        var key = ticket.GetProperty("uploadKey").GetString()!;
        (await MediaApi.PutToTicketAsync(ticket, Bytes(SealedFile.MinBytes - 1))).EnsureSuccessStatusCode();

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("too small", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task The_smallest_sealed_file_is_accepted()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(SealedFile.MinBytes));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task When_a_later_file_is_refused_the_copies_already_made_are_taken_back()
    {
        using var host = WithSetting("Storage:MaxFileBytes", "1024");
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = Authorized(host, alice);
        var good = await client.UploadFileAsync(conversation, Bytes(100));
        var bigTicket = await client.GetUploadTicketAsync(conversation, 100);
        var big = bigTicket.GetProperty("uploadKey").GetString()!;
        await _factory.PutObjectAsync(big, Bytes(5000));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [good, big]);

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
                key => _factory.PutObjectAsync(key, Bytes(900))));
        }));
        using var client = Authorized(host, alice);
        var key = await client.UploadFileAsync(conversation, Bytes(100));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

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
        var key = await client.UploadFileAsync(conversation, Bytes(64));
        await _factory.ExecuteDbAsync(async db =>
        {
            db.ConversationMembers.Single(m => m.ConversationId == conversation && m.UserId == bob.Id).LeftAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await _factory.ObjectExistsAsync(key));
        Assert.Empty(await _factory.ObjectKeysAsync($"attachments/{conversation}/"));
    }

    [Fact]
    public async Task A_deactivated_account_cannot_send_files()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));
        await _factory.DeactivateAsync(alice);

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

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
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var response = await client.PostMessageWithFilesAsync(conversation, null, [key]);

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
        var key = await client.UploadFileAsync(conversation, Bytes(64));
        var clientMessageId = Guid.NewGuid();
        var first = await client.PostMessageWithFilesAsync(conversation, "once", [key], clientMessageId);
        var sent = await first.Content.ReadFromJsonAsync<JsonElement>();

        var retry = await client.PostMessageWithFilesAsync(conversation, "once", [key], clientMessageId);

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
        var key = await client.UploadFileAsync(conversation, Bytes(64));
        var clientMessageId = Guid.NewGuid();
        var sent = await (await client.PostMessageWithFilesAsync(conversation, null, [key], clientMessageId))
            .Content.ReadFromJsonAsync<JsonElement>();
        using var host = HostWhoseFirstLookupFindsNothing();
        using var late = Authorized(host, alice);

        var response = await late.PostMessageWithFilesAsync(conversation, null, [key], clientMessageId);

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
        var key = await client.UploadFileAsync(conversation, Bytes(64));
        var clientMessageId = Guid.NewGuid();
        await _factory.AddMessageAsync(conversation, alice.Id, "winner", DateTime.UtcNow.AddMinutes(-1), clientMessageId);
        using var host = HostWhoseFirstLookupFindsNothing();
        using var loser = Authorized(host, alice);

        var response = await loser.PostMessageWithFilesAsync(conversation, null, [key], clientMessageId);

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
        var key = await client.UploadFileAsync(conversation, Bytes(64));
        var clientMessageId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.PostMessageWithFilesAsync(conversation, null, [key], clientMessageId)));

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
    public async Task The_log_has_ids_and_a_count_and_never_the_text_or_a_key()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);
        var key = await client.UploadFileAsync(conversation, Bytes(64));

        var message = await client.SendMessageWithFilesAsync(conversation, "my secret caption", key);

        Assert.Contains(capture.Entries, e => e.Contains(message.MessageIdOf().ToString()) && e.Contains("with 1 attachments"));
        Assert.DoesNotContain(capture.Entries, e => e.Contains("secret caption") || e.Contains(key));
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
