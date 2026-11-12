using System.IdentityModel.Tokens.Jwt;
using System.Net;
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
    // The id of the person a client is signed in as, read from its token. A client that is not signed in is refused before its request
    // is read, so who it is does not matter and it is nobody.
    public static Guid CallerId(this HttpClient client)
    {
        var token = client.DefaultRequestHeaders.Authorization?.Parameter;
        return token is null ? Guid.Empty : Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Subject);
    }

    // Starting a conversation needs the first key period, made for the people in it. The tests act as a browser would, with bytes of
    // the right shape, and give the conversation an id of their own as a browser does.
    public static Task<HttpResponseMessage> PostDirectAsync(this HttpClient client, Guid otherUserId, Guid? id = null) =>
        client.PostAsJsonAsync("/api/conversations/direct", new
        {
            id = id ?? Guid.NewGuid(),
            otherUserId,
            epoch = EpochBody.For(1, client.CallerId(), otherUserId),
        });

    public static async Task<JsonElement> CreateDirectAsync(this HttpClient client, Guid otherUserId)
    {
        var response = await client.PostDirectAsync(otherUserId);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> PostGroupAsync(this HttpClient client, string name, params Guid[] memberIds) =>
        client.PostAsJsonAsync("/api/conversations/group", new
        {
            id = Guid.NewGuid(),
            name,
            memberIds,
            epoch = EpochBody.For(1, [client.CallerId(), .. memberIds.Where(id => id != client.CallerId()).Distinct()]),
        });

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

    // ##### Managing a group #####

    // Adding people makes a new key period for everyone who will be in the group: the people there now and the ones named. Someone
    // who may not look at the group cannot say who is in it; the request then carries only who was named, and is refused for the
    // reason it would be refused anyway.
    public static async Task<HttpResponseMessage> AddMembersResponseAsync(
        this HttpClient client, Guid conversationId, params Guid[] userIds)
    {
        var epoch = await client.NextEpochForAddingAsync(conversationId, userIds);
        return await client.PostAsJsonAsync($"/api/conversations/{conversationId}/members", new { userIds, epoch });
    }

    private static async Task<EpochBody> NextEpochForAddingAsync(this HttpClient client, Guid conversationId, Guid[] added)
    {
        var current = await client.GetAsync($"/api/conversations/{conversationId}");
        var members = await client.GetMembersResponseAsync(conversationId);
        if (current.StatusCode != HttpStatusCode.OK || members.StatusCode != HttpStatusCode.OK)
            return EpochBody.For(1, [.. new[] { client.CallerId() }.Concat(added).Distinct()]);

        var keyEpoch = (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("keyEpoch").GetInt32();
        var present = (await members.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Select(m => m.GetProperty("userId").GetGuid());

        return EpochBody.For(keyEpoch + 1, present.Concat(added).Distinct().ToArray());
    }

    // Adds people and returns the people of the group as the server answers.
    public static async Task<List<JsonElement>> AddMembersAsync(
        this HttpClient client, Guid conversationId, params Guid[] userIds)
    {
        var response = await client.AddMembersResponseAsync(conversationId, userIds);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    public static Task<HttpResponseMessage> RemoveMemberResponseAsync(
        this HttpClient client, Guid conversationId, Guid userId) =>
        client.DeleteAsync($"/api/conversations/{conversationId}/members/{userId}");

    public static Task<HttpResponseMessage> ChangeRoleResponseAsync(
        this HttpClient client, Guid conversationId, Guid userId, string? role) =>
        client.PutAsJsonAsync($"/api/conversations/{conversationId}/members/{userId}/role", new { role });

    public static Task<HttpResponseMessage> LeaveResponseAsync(this HttpClient client, Guid conversationId) =>
        client.PostAsync($"/api/conversations/{conversationId}/leave", null);

    public static Task<HttpResponseMessage> RenameResponseAsync(this HttpClient client, Guid conversationId, string? name) =>
        client.PatchAsJsonAsync($"/api/conversations/{conversationId}", new { name });

    // The people of a group as a list of (user id, role), the order the server gives them in.
    public static List<(Guid UserId, string Role)> PeopleOf(this IEnumerable<JsonElement> members) =>
        members.Select(m => (m.GetProperty("userId").GetGuid(), m.GetProperty("role").GetString()!)).ToList();

    public static async Task<List<JsonElement>> ReadMembersAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

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
                Ciphertext = deleted ? null : FakeMessage.Blob(1, content),
                KeyEpoch = 1,
                CreatedAt = createdAt,
                ClientMessageId = clientMessageId ?? Guid.NewGuid(),
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
                Ciphertext = FakeMessage.Blob(1, $"{prefix} {i}"),
                KeyEpoch = 1,
                ClientMessageId = Guid.NewGuid(),
                CreatedAt = firstAt + i * step,
            }));
            await db.SaveChangesAsync();
        });
}
