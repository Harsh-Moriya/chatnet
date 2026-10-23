using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Messaging.Models;
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

    // Tells the server the caller has read the conversation up to and including that message.
    public static Task<HttpResponseMessage> MarkReadResponseAsync(
        this HttpClient client, Guid conversationId, Guid messageId) =>
        client.PostAsJsonAsync($"/api/conversations/{conversationId}/read", new { messageId });

    public static async Task MarkReadAsync(this HttpClient client, Guid conversationId, Guid messageId)
    {
        var response = await client.MarkReadResponseAsync(conversationId, messageId);
        response.EnsureSuccessStatusCode();
    }

    // Mutes (or unmutes) the conversation for the caller.
    public static Task<HttpResponseMessage> SetMutedResponseAsync(
        this HttpClient client, Guid conversationId, bool isMuted) =>
        client.PutAsJsonAsync($"/api/conversations/{conversationId}/mute", new { isMuted });

    public static async Task SetMutedAsync(this HttpClient client, Guid conversationId, bool isMuted)
    {
        var response = await client.SetMutedResponseAsync(conversationId, isMuted);
        response.EnsureSuccessStatusCode();
    }

    public static Task<HttpResponseMessage> GetMembersResponseAsync(this HttpClient client, Guid conversationId) =>
        client.GetAsync($"/api/conversations/{conversationId}/members");

    public static async Task<List<JsonElement>> GetMembersAsync(this HttpClient client, Guid conversationId)
    {
        var response = await client.GetMembersResponseAsync(conversationId);
        response.EnsureSuccessStatusCode();
        var members = await response.Content.ReadFromJsonAsync<JsonElement>();
        return members.EnumerateArray().ToList();
    }

    // The unread count of one conversation as the caller sees it in their list.
    public static async Task<int> UnreadInListAsync(this HttpClient client, Guid conversationId) =>
        (await client.ListConversationsAsync()).Single(c => c.IdOf() == conversationId).UnreadOf();

    public static Guid IdOf(this JsonElement conversation) => conversation.GetProperty("id").GetGuid();

    public static int UnreadOf(this JsonElement conversation) => conversation.GetProperty("unreadCount").GetInt32();

    // The stored read position of a member: null until they have read or sent something.
    public static Task<DateTime?> LastReadAtAsync(this ChatApiFactory factory, Guid conversationId, Guid userId) =>
        factory.QueryDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == userId)
            .Select(m => m.LastReadAt)
            .SingleAsync());

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

    // Inserts a message straight into the database. The send endpoint always stamps the current
    // time and cannot produce a deleted message, so tests that need control of the timestamp or
    // of the deleted state, or that need thousands of messages, arrange them here.
    public static Task AddMessageAsync(
        this ChatApiFactory factory, Guid conversationId, Guid senderId, string content,
        DateTime createdAt, Guid? clientMessageId = null, bool deleted = false) =>
        factory.ExecuteDbAsync(async db =>
        {
            db.Messages.Add(new Message
            {
                ConversationId = conversationId,
                SenderId = senderId,
                Content = content,
                CreatedAt = createdAt,
                ClientMessageId = clientMessageId,
                IsDeleted = deleted,
                DeletedAt = deleted ? createdAt : null,
                DeletedById = deleted ? senderId : null,
            });
            await db.SaveChangesAsync();
        });

    // History only shows messages sent since a member joined, and a conversation made by a test has
    // "just now" as its join time. Tests that arrange messages with past timestamps first move the
    // join time of every member back, so those messages count as sent after they joined.
    public static Task BackdateJoinAsync(this ChatApiFactory factory, Guid conversationId) =>
        factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, DateTime.UtcNow.AddDays(-1))));

    // Inserts "count" messages with content "{prefix} 0", "{prefix} 1" and so on, one step apart
    // starting at firstAt, in a single save.
    public static Task AddMessagesAsync(
        this ChatApiFactory factory, Guid conversationId, Guid senderId, int count,
        DateTime firstAt, TimeSpan step, string prefix = "message") =>
        factory.ExecuteDbAsync(async db =>
        {
            db.Messages.AddRange(Enumerable.Range(0, count).Select(i => new Message
            {
                ConversationId = conversationId,
                SenderId = senderId,
                Content = $"{prefix} {i}",
                CreatedAt = firstAt + i * step,
            }));
            await db.SaveChangesAsync();
        });
}
