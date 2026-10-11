using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Infrastructure;

// Thin wrappers over the conversation endpoints so tests read as intent ("create a direct chat
// with Bob") instead of repeating request plumbing. They return the raw JSON a client receives,
// because the tests assert on the public contract, not on server-side types.
public static class ConversationApi
{
    public static Task<HttpResponseMessage> PostDirectAsync(this HttpClient client, Guid otherUserId) =>
        client.PostAsJsonAsync("/api/conversations/direct", new { otherUserId });

    public static async Task<JsonElement> CreateDirectAsync(this HttpClient client, Guid otherUserId)
    {
        var response = await client.PostDirectAsync(otherUserId);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> PostGroupAsync(this HttpClient client, string name, params Guid[] memberIds) =>
        client.PostAsJsonAsync("/api/conversations/group", new { name, memberIds });

    public static async Task<JsonElement> CreateGroupAsync(this HttpClient client, string name, params Guid[] memberIds)
    {
        var response = await client.PostGroupAsync(name, memberIds);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<List<JsonElement>> ListConversationsAsync(this HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/conversations");
        return list.EnumerateArray().ToList();
    }

    public static Guid IdOf(this JsonElement conversation) => conversation.GetProperty("id").GetGuid();

    public static async Task<List<JsonElement>> SearchUsersAsync(this HttpClient client, string query)
    {
        var results = await client.GetFromJsonAsync<JsonElement>($"/api/users?query={Uri.EscapeDataString(query)}");
        return results.EnumerateArray().ToList();
    }

    // The API has no way to deactivate a user yet, so tests arrange it in the database.
    public static Task DeactivateAsync(this ChatApiFactory factory, TestUser user) =>
        factory.ExecuteDbAsync(db => db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeactivated, true)));
}
