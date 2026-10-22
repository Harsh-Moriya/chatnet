using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Tests.Infrastructure;

// Wrappers for uploading and attaching files, in the same spirit as MessageApi: a test reads as what a client
// does (ask for permission, send the file to the bucket, name it in a message) and asserts on the JSON it gets.
public static class MediaApi
{
    // The bucket is another host to the API, and what a browser is to it is a plain HTTP client.
    private static readonly HttpClient Bucket = new();

    public static Task<HttpResponseMessage> RequestUploadAsync(
        this HttpClient client, Guid conversationId, string? fileName, string? contentType, long sizeBytes) =>
        client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/uploads", new { fileName, contentType, sizeBytes });

    public static async Task<JsonElement> GetUploadTicketAsync(
        this HttpClient client, Guid conversationId, string fileName = "photo.png", string contentType = "image/png", long sizeBytes = 100)
    {
        var response = await client.RequestUploadAsync(conversationId, fileName, contentType, sizeBytes);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Sends a file to the address of a ticket, as a browser does, and returns what the bucket answered.
    public static async Task<HttpResponseMessage> PutToTicketAsync(JsonElement ticket, byte[] content, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, ticket.GetProperty("url").GetString())
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return await Bucket.SendAsync(request);
    }

    // Asks for permission and sends the file: what the client does for each file before the message is sent.
    // Returns the key the message names.
    public static async Task<string> UploadFileAsync(
        this HttpClient client, Guid conversationId, byte[] content, string contentType = "image/png", string fileName = "photo.png")
    {
        var ticket = await client.GetUploadTicketAsync(conversationId, fileName, contentType, content.Length);
        (await PutToTicketAsync(ticket, content, contentType)).EnsureSuccessStatusCode();

        return ticket.GetProperty("uploadKey").GetString()!;
    }

    public static Task<HttpResponseMessage> PostMessageWithFilesAsync(
        this HttpClient client, Guid conversationId, string? content, IEnumerable<(string Key, string Name)> files, Guid? clientMessageId = null) =>
        client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new
            {
                content,
                clientMessageId,
                attachments = files.Select(f => new { uploadKey = f.Key, fileName = f.Name }).ToArray(),
            });

    public static async Task<JsonElement> SendMessageWithFilesAsync(
        this HttpClient client, Guid conversationId, string? content, params (string Key, string Name)[] files)
    {
        var response = await client.PostMessageWithFilesAsync(conversationId, content, files);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> GetAttachmentLinkResponseAsync(this HttpClient client, Guid attachmentId) =>
        client.GetAsync($"/api/attachments/{attachmentId}/url");

    // The files of a message as the API returned them.
    public static List<JsonElement> AttachmentsOf(this JsonElement message) =>
        message.GetProperty("attachments").EnumerateArray().ToList();

    public static string UrlOf(this JsonElement attachment) => attachment.GetProperty("url").GetString()!;

    // What a browser gets by following a link from a message.
    public static async Task<HttpResponseMessage> FollowAsync(JsonElement attachment) =>
        await Bucket.GetAsync(attachment.UrlOf());

    // A conversation of two people, as most of the tests need.
    public static async Task<(TestUser Alice, TestUser Bob, Guid Conversation)> DirectChatAsync(this ChatApiFactory factory)
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        using var client = factory.CreateClientFor(alice);

        return (alice, bob, (await client.CreateDirectAsync(bob.Id)).IdOf());
    }

    // Arranges a message with files without the API: for states it cannot produce (a message from the past, a
    // deleted one) and for files that point at nothing. Returns the message and the ids of its files.
    public static async Task<(Guid MessageId, List<Guid> AttachmentIds)> AddMessageWithFilesAsync(
        this ChatApiFactory factory, Guid conversationId, Guid senderId, string content, DateTime createdAt,
        int fileCount, bool deleted = false, string contentType = "image/png")
    {
        var message = new Message
        {
            ConversationId = conversationId,
            SenderId = senderId,
            Content = content,
            CreatedAt = createdAt,
            IsDeleted = deleted,
            DeletedAt = deleted ? createdAt : null,
            DeletedById = deleted ? senderId : null,
            Attachments = Enumerable.Range(0, fileCount).Select(i => new MessageAttachment
            {
                Id = Guid.NewGuid(),
                StorageKey = $"attachments/{conversationId}/{Guid.NewGuid()}",
                Type = AttachmentType.Image,
                FileName = $"file {i}.png",
                ContentType = contentType,
                FileSizeBytes = 100,
                CreatedAt = createdAt.AddTicks(i * 10),
            }).ToList(),
        };

        await factory.ExecuteDbAsync(async db =>
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync();
        });

        return (message.Id, message.Attachments.Select(a => a.Id).ToList());
    }
}
