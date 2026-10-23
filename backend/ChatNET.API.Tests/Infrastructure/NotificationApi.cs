using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Notifications.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Infrastructure;

// Wrappers over the notification endpoints and for arranging notifications that the application would only make
// as a side effect of something else, in the same spirit as MessageApi: a test reads as intent and asserts on the
// JSON a client receives.
public static class NotificationApi
{
    public static Task<HttpResponseMessage> GetNotificationsResponseAsync(this HttpClient client, Guid? before = null) =>
        client.GetAsync(before is { } id ? $"/api/notifications?before={id}" : "/api/notifications");

    public static async Task<JsonElement> GetNotificationsAsync(this HttpClient client, Guid? before = null)
    {
        var response = await client.GetNotificationsResponseAsync(before);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Without ids, everything is marked read; "ids: null" and a missing property mean the same.
    public static Task<HttpResponseMessage> MarkNotificationsReadResponseAsync(this HttpClient client, IEnumerable<Guid>? ids = null) =>
        client.PostAsJsonAsync("/api/notifications/read", new { ids = ids?.ToArray() });

    public static Task<HttpResponseMessage> DismissNotificationResponseAsync(this HttpClient client, Guid id) =>
        client.DeleteAsync($"/api/notifications/{id}");

    public static List<JsonElement> ItemsOf(this JsonElement list) => list.GetProperty("items").EnumerateArray().ToList();

    public static int UnreadCountOf(this JsonElement list) => list.GetProperty("unreadCount").GetInt32();

    public static List<Guid> IdsOf(this JsonElement list) => list.ItemsOf().Select(n => n.GetProperty("id").GetGuid()).ToList();

    // Inserts notifications straight into the database. "isRead" decides each one from its position (0 is the
    // oldest), so a list can interleave read and unread ones; the stamps are one second apart.
    public static async Task<List<Guid>> AddNotificationsAsync(
        this ChatApiFactory factory, Guid userId, Guid actorId, Guid? conversationId, int count, DateTime start,
        Func<int, bool>? isRead = null, NotificationType type = NotificationType.Mention, Guid? messageId = null)
    {
        var notifications = Enumerable.Range(0, count).Select(i => new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ActorId = actorId,
            ConversationId = conversationId,
            MessageId = messageId,
            Type = type,
            IsRead = isRead?.Invoke(i) ?? false,
            CreatedAt = start.AddSeconds(i),
        }).ToList();

        await factory.ExecuteDbAsync(async db =>
        {
            db.Notifications.AddRange(notifications);
            await db.SaveChangesAsync();
        });

        return notifications.Select(n => n.Id).ToList();
    }

    public static Task<List<Notification>> NotificationRowsAsync(this ChatApiFactory factory, Guid userId) =>
        factory.QueryDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderBy(n => n.CreatedAt).ToListAsync());
}
