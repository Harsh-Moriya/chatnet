using System.Net.Http.Json;
using System.Text.Json;

namespace ChatNET.API.Tests.Infrastructure;

// Wrappers for the search endpoint, in the same spirit as MessageApi: a test reads as what a client does and asserts on the JSON.
public static class SearchApi
{
    public static Task<HttpResponseMessage> SearchResponseAsync(
        this HttpClient client, string? q, Guid? conversationId = null, Guid? before = null)
    {
        var query = new List<string>();
        if (q is not null) query.Add($"q={Uri.EscapeDataString(q)}");
        if (conversationId is { } c) query.Add($"conversationId={c}");
        if (before is { } b) query.Add($"before={b}");

        return client.GetAsync("/api/search/messages" + (query.Count == 0 ? "" : "?" + string.Join("&", query)));
    }

    public static async Task<List<JsonElement>> SearchAsync(
        this HttpClient client, string q, Guid? conversationId = null, Guid? before = null)
    {
        var response = await client.SearchResponseAsync(q, conversationId, before);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    public static Guid HitIdOf(this JsonElement hit) => hit.GetProperty("messageId").GetGuid();

    // The whole snippet as one string, and only the parts that matched.
    public static string SnippetOf(this JsonElement hit) =>
        string.Concat(hit.GetProperty("snippet").EnumerateArray().Select(p => p.GetProperty("text").GetString()));

    public static string MatchedOf(this JsonElement hit) =>
        string.Concat(hit.GetProperty("snippet").EnumerateArray()
            .Where(p => p.GetProperty("match").GetBoolean())
            .Select(p => p.GetProperty("text").GetString()));

    public static string? FileNameOf(this JsonElement hit) =>
        hit.GetProperty("fileName").ValueKind == JsonValueKind.Null ? null : hit.GetProperty("fileName").GetString();
}
