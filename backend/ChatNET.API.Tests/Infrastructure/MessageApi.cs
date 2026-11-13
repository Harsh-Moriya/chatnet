using System.Net.Http.Json;
using System.Text.Json;

namespace ChatNET.API.Tests.Infrastructure;

// Thin wrappers over the message endpoints, in the same spirit as ConversationApi: tests read as
// intent and assert on the JSON a client receives.
public static class MessageApi
{
    // The epoch a conversation is in now, which is what a message must be sealed under. 1 for a conversation the caller cannot read
    // (the send is then refused for another reason) and for one that has no epoch yet (it is refused as out of date).
    public static async Task<int> CurrentEpochAsync(this HttpClient client, Guid conversationId)
    {
        var response = await client.GetAsync($"/api/conversations/{conversationId}");
        if (!response.IsSuccessStatusCode)
            return 1;

        return Math.Max(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("keyEpoch").GetInt32());
    }

    // What a browser does when a removal or a leave marked the epoch as due, before it says anything more: stores the next epoch,
    // made for the people who are in the conversation now. Answers the epoch it stored.
    public static async Task<int> StoreNextEpochAsync(this HttpClient client, Guid conversationId)
    {
        var members = (await client.GetMembersAsync(conversationId)).Select(m => m.GetProperty("userId").GetGuid()).ToArray();
        var conversation = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversationId}");
        var next = conversation.GetProperty("keyEpoch").GetInt32() + 1;

        await client.StoreEpochAsync(conversationId, EpochBody.For(next, members));
        return next;
    }

    // How many times the message has been edited, as the history shows it. The next edit is the one after. 0 when the message cannot
    // be found by this caller, and the edit is then refused for that reason.
    public static async Task<int> EditCountOfAsync(this HttpClient client, Guid conversationId, Guid messageId)
    {
        var response = await client.GetHistoryResponseAsync(conversationId, around: messageId);
        if (!response.IsSuccessStatusCode)
            return 0;

        var page = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var found = page.Where(m => m.MessageIdOf() == messageId).ToList();
        return found.Count == 0 ? 0 : found[0].GetProperty("editCount").GetInt32();
    }

    // The text is sealed as a blob of the shape the server accepts (see FakeMessage), under the epoch the conversation is in unless the
    // test names one. A null text sends no message at all, for the tests of what is refused. The client id is the one given, or a new one.
    public static async Task<HttpResponseMessage> PostMessageAsync(
        this HttpClient client, Guid conversationId, string? content, Guid? clientMessageId = null,
        int? epoch = null, IEnumerable<Guid>? mentions = null)
    {
        var ciphertext = content is null ? null : FakeMessage.Ciphertext(epoch ?? await client.CurrentEpochAsync(conversationId), content);

        return await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new { ciphertext, clientMessageId = clientMessageId ?? Guid.NewGuid(), mentionedUserIds = mentions?.ToArray() });
    }

    public static async Task<JsonElement> SendMessageAsync(
        this HttpClient client, Guid conversationId, string content, Guid? clientMessageId = null,
        int? epoch = null, IEnumerable<Guid>? mentions = null)
    {
        var response = await client.PostMessageAsync(conversationId, content, clientMessageId, epoch, mentions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // "before" asks for the page of older messages that ends just above that message; "after" for the
    // page of newer messages that starts just below it. Neither means the latest page.
    public static Task<HttpResponseMessage> GetHistoryResponseAsync(
        this HttpClient client, Guid conversationId, Guid? before = null, Guid? after = null, Guid? around = null)
    {
        var query = new List<string>();
        if (before is { } b) query.Add($"before={b}");
        if (after is { } a) query.Add($"after={a}");
        if (around is { } r) query.Add($"around={r}");

        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return client.GetAsync($"/api/conversations/{conversationId}/messages{suffix}");
    }

    public static async Task<List<JsonElement>> GetHistoryAsync(
        this HttpClient client, Guid conversationId, Guid? before = null, Guid? after = null, Guid? around = null)
    {
        var response = await client.GetHistoryResponseAsync(conversationId, before, after, around);
        response.EnsureSuccessStatusCode();
        var history = await response.Content.ReadFromJsonAsync<JsonElement>();
        return history.EnumerateArray().ToList();
    }

    // The edit is the next one after the message's own count unless the test says which, and is sealed under the epoch the
    // conversation is in unless the test names one.
    public static async Task<HttpResponseMessage> PatchMessageAsync(
        this HttpClient client, Guid conversationId, Guid messageId, string? content,
        int? editCount = null, int? epoch = null, IEnumerable<Guid>? mentions = null)
    {
        var ciphertext = content is null ? null : FakeMessage.Ciphertext(epoch ?? await client.CurrentEpochAsync(conversationId), content);
        var count = editCount ?? await client.EditCountOfAsync(conversationId, messageId) + 1;

        return await client.PatchAsJsonAsync(
            $"/api/conversations/{conversationId}/messages/{messageId}",
            new { ciphertext, editCount = count, mentionedUserIds = mentions?.ToArray() });
    }

    public static async Task<JsonElement> EditMessageAsync(
        this HttpClient client, Guid conversationId, Guid messageId, string content,
        int? editCount = null, int? epoch = null, IEnumerable<Guid>? mentions = null)
    {
        var response = await client.PatchMessageAsync(conversationId, messageId, content, editCount, epoch, mentions);
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

    // The number of the latest change to a message in its conversation, as the server hands it out.
    public static long ChangeSeqOf(this JsonElement message) => message.GetProperty("changeSeq").GetInt64();

    // One page of the changes feed: the messages that changed after the number "since" (0 for all of them). A null "since" asks without it.
    public sealed record ChangesPage(List<JsonElement> Changes, long Cursor, bool More);

    public static Task<HttpResponseMessage> GetChangesResponseAsync(this HttpClient client, Guid conversationId, long? since) =>
        client.GetAsync($"/api/conversations/{conversationId}/changes" + (since is { } s ? $"?since={s}" : ""));

    public static async Task<ChangesPage> GetChangesAsync(this HttpClient client, Guid conversationId, long since)
    {
        var response = await client.GetChangesResponseAsync(conversationId, since);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return new ChangesPage(
            body.GetProperty("changes").EnumerateArray().ToList(), body.GetProperty("cursor").GetInt64(), body.GetProperty("more").GetBoolean());
    }

    // What the conversation holds, from the start, by following the feed page by page: the current state of every message.
    public static async Task<List<JsonElement>> ReadAllChangesAsync(this HttpClient client, Guid conversationId, long since = 0)
    {
        var all = new List<JsonElement>();
        ChangesPage page;
        do
        {
            page = await client.GetChangesAsync(conversationId, since);
            all.AddRange(page.Changes);
            since = page.Cursor;
        }
        while (page.More);

        return all;
    }

    // The text of a message, or of the last message of a conversation, as the tests' blobs carry it (see FakeMessage). Null when it is
    // deleted. The server hands on the blob and cannot read it, so this is the test reading what it put in.
    public static string? ContentOf(this JsonElement message) =>
        FakeMessage.TextOf(message.GetProperty("ciphertext").GetString());
}
