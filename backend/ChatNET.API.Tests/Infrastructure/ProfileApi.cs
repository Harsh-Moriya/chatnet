using System.Net.Http.Json;
using System.Text.Json;

namespace ChatNET.API.Tests.Infrastructure;

// Wrappers for the profile and picture endpoints, in the same spirit as ConversationApi: a test reads as what a client does
// (ask for permission, send the picture to the bucket, name it) and asserts on the JSON it gets.
public static class ProfileApi
{
    // A small valid PNG: the picture itself is never decoded by the server, but this is what a browser would send.
    public static readonly byte[] SmallPicture = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static Task<HttpResponseMessage> GetMeResponseAsync(this HttpClient client) => client.GetAsync("/api/users/me");

    public static Task<HttpResponseMessage> UpdateProfileResponseAsync(this HttpClient client, string? displayName) =>
        client.PatchAsJsonAsync("/api/users/me", new { displayName });

    // The ticket for a picture of a person ("me") or of a group.
    public static Task<HttpResponseMessage> AvatarUploadResponseAsync(
        this HttpClient client, Guid? conversationId, string? contentType, long sizeBytes) =>
        client.PostAsJsonAsync(AvatarPath(conversationId) + "/uploads", new { contentType, sizeBytes });

    public static Task<HttpResponseMessage> SetAvatarResponseAsync(
        this HttpClient client, Guid? conversationId, string? uploadKey) =>
        client.PutAsJsonAsync(AvatarPath(conversationId), new { uploadKey });

    public static Task<HttpResponseMessage> RemoveAvatarResponseAsync(this HttpClient client, Guid? conversationId) =>
        client.DeleteAsync(AvatarPath(conversationId));

    // Asks for permission and sends the picture: what the client does before it names the upload. Returns the key.
    public static async Task<string> UploadAvatarAsync(
        this HttpClient client, Guid? conversationId, byte[]? content = null, string contentType = "image/png")
    {
        content ??= SmallPicture;
        var response = await client.AvatarUploadResponseAsync(conversationId, contentType, content.Length);
        response.EnsureSuccessStatusCode();
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        (await MediaApi.PutToTicketAsync(ticket, content, contentType)).EnsureSuccessStatusCode();

        return ticket.GetProperty("uploadKey").GetString()!;
    }

    // Uploads a picture and makes it the one. Returns the address the API gave for it.
    public static async Task<string> SetAvatarAsync(this HttpClient client, Guid? conversationId = null)
    {
        var key = await client.UploadAvatarAsync(conversationId);
        var response = await client.SetAvatarResponseAsync(conversationId, key);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avatarUrl").GetString()!;
    }

    public static async Task<JsonElement> GetMeAsync(this HttpClient client)
    {
        var response = await client.GetMeResponseAsync();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static string? AvatarUrlOf(this JsonElement element)
        => element.GetProperty("avatarUrl").ValueKind == JsonValueKind.Null ? null : element.GetProperty("avatarUrl").GetString();

    // The stored key behind an address the API gave out: the address is the key under /api.
    public static string KeyOf(string avatarUrl) => avatarUrl["/api/".Length..];

    private static string AvatarPath(Guid? conversationId)
        => conversationId is { } id ? $"/api/conversations/{id}/avatar" : "/api/users/me/avatar";
}
