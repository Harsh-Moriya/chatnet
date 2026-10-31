using System.Net.Http.Json;
using System.Text.Json;

namespace ChatNET.API.Tests.Infrastructure;

// Thin wrappers over the message endpoints, in the same spirit as ConversationApi: tests read as
// intent and assert on the JSON a client receives.
public static class MessageApi
{
    public static Task<HttpResponseMessage> PostMessageAsync(
        this HttpClient client, Guid conversationId, string? content, Guid? clientMessageId = null) =>
        client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", new { content, clientMessageId });

    public static async Task<JsonElement> SendMessageAsync(
        this HttpClient client, Guid conversationId, string content, Guid? clientMessageId = null)
    {
        var response = await client.PostMessageAsync(conversationId, content, clientMessageId);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // "before" asks for the page of older messages that ends just above that message; "after" for the
    // page of newer messages that starts just below it. Neither means the latest page.
    public static Task<HttpResponseMessage> GetHistoryResponseAsync(
        this HttpClient client, Guid conversationId, Guid? before = null, Guid? after = null)
    {
        var query = new List<string>();
        if (before is { } b) query.Add($"before={b}");
        if (after is { } a) query.Add($"after={a}");

        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return client.GetAsync($"/api/conversations/{conversationId}/messages{suffix}");
    }

    public static async Task<List<JsonElement>> GetHistoryAsync(
        this HttpClient client, Guid conversationId, Guid? before = null, Guid? after = null)
    {
        var response = await client.GetHistoryResponseAsync(conversationId, before, after);
        response.EnsureSuccessStatusCode();
        var history = await response.Content.ReadFromJsonAsync<JsonElement>();
        return history.EnumerateArray().ToList();
    }

    public static Task<HttpResponseMessage> PatchMessageAsync(
        this HttpClient client, Guid conversationId, Guid messageId, string? content) =>
        client.PatchAsJsonAsync($"/api/conversations/{conversationId}/messages/{messageId}", new { content });

    public static async Task<JsonElement> EditMessageAsync(
        this HttpClient client, Guid conversationId, Guid messageId, string content)
    {
        var response = await client.PatchMessageAsync(conversationId, messageId, content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> DeleteMessageResponseAsync(
        this HttpClient client, Guid conversationId, Guid messageId) =>
        client.DeleteAsync($"/api/conversations/{conversationId}/messages/{messageId}");

    public static async Task<JsonElement> DeleteMessageAsync(this HttpClient client, Guid conversationId, Guid messageId)
    {
        var response = await client.DeleteMessageResponseAsync(conversationId, messageId);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Guid MessageIdOf(this JsonElement message) => message.GetProperty("id").GetGuid();

    public static string? ContentOf(this JsonElement message) =>
        message.GetProperty("content").ValueKind == JsonValueKind.Null
            ? null
            : message.GetProperty("content").GetString();
}
