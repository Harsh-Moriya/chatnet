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

    public static Task<HttpResponseMessage> GetHistoryResponseAsync(this HttpClient client, Guid conversationId) =>
        client.GetAsync($"/api/conversations/{conversationId}/messages");

    public static async Task<List<JsonElement>> GetHistoryAsync(this HttpClient client, Guid conversationId)
    {
        var response = await client.GetHistoryResponseAsync(conversationId);
        response.EnsureSuccessStatusCode();
        var history = await response.Content.ReadFromJsonAsync<JsonElement>();
        return history.EnumerateArray().ToList();
    }

    public static Guid MessageIdOf(this JsonElement message) => message.GetProperty("id").GetGuid();

    public static string? ContentOf(this JsonElement message) =>
        message.GetProperty("content").ValueKind == JsonValueKind.Null
            ? null
            : message.GetProperty("content").GetString();
}
