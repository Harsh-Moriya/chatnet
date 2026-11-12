using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Keys;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// Sending a message the server cannot read: what it insists on (the shape of the blob, the client id, the epoch the conversation is in)
// and what it does with the names a sender says the message holds.
[Collection(IntegrationTestCollection.Name)]
public class EncryptedSendTests
{
    private readonly ChatApiFactory _factory;

    public EncryptedSendTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(TestUser Owner, TestUser Member, TestUser Other, Guid Group)> GroupAsync()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var other = await _factory.RegisterUserAsync("other");
        using var client = _factory.CreateClientFor(owner);
        var group = await client.CreateGroupAsync("Team", member.Id, other.Id);
        return (owner, member, other, group.IdOf());
    }

    // The notifications of a person that are about a mention: creating a group tells its members too, which is not what these look at.
    private async Task<List<JsonElement>> MentionsOfAsync(TestUser person)
    {
        using var client = _factory.CreateClientFor(person);
        return (await client.GetNotificationsAsync()).ItemsOf().Where(n => n.GetProperty("type").GetString() == "Mention").ToList();
    }

    private Task<int> MessageCountAsync(Guid conversationId) =>
        _factory.QueryDbAsync(db => db.Messages.CountAsync(m => m.ConversationId == conversationId));

    private Task<DateTime> UpdatedAtAsync(Guid conversationId) =>
        _factory.QueryDbAsync(db => db.Conversations.Where(c => c.Id == conversationId).Select(c => c.UpdatedAt).SingleAsync());

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, Guid conversationId, object body) =>
        client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", body);

    // ##### What is stored and returned #####

    [Fact]
    public async Task A_message_is_stored_as_the_blob_that_was_sent_under_the_epoch_in_its_header()
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        var clientId = Guid.NewGuid();
        var blob = FakeMessage.Blob(1, "sealed words");

        var response = await PostRawAsync(client, group, new { ciphertext = KeyFormats.Encode(blob), clientMessageId = clientId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(KeyFormats.Encode(blob), message.GetProperty("ciphertext").GetString());
        Assert.Equal(1, message.GetProperty("keyEpoch").GetInt32());
        Assert.Equal(clientId, message.GetProperty("clientMessageId").GetGuid());
        Assert.False(message.TryGetProperty("content", out _));

        var stored = await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == message.MessageIdOf()));
        Assert.Equal(blob, stored.Ciphertext);
        Assert.Equal(1, stored.KeyEpoch);
        Assert.Equal(clientId, stored.ClientMessageId);
        Assert.Empty(stored.MentionedUserIds);
    }

    [Fact]
    public async Task The_epoch_is_the_one_in_the_header_of_the_blob_and_the_server_makes_no_other()
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        await client.AddMembersAsync(group, (await _factory.RegisterUserAsync("extra")).Id);

        var sent = await client.SendMessageAsync(group, "second epoch", epoch: 2);

        Assert.Equal(2, sent.GetProperty("keyEpoch").GetInt32());
        Assert.Equal(2, (await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == sent.MessageIdOf()))).KeyEpoch);
    }

    [Fact]
    public async Task A_deleted_message_has_no_blob_and_keeps_the_epoch_it_was_sealed_in()
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        var sent = await client.SendMessageAsync(group, "to be removed");

        var deleted = await client.DeleteMessageAsync(group, sent.MessageIdOf());

        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("ciphertext").ValueKind);
        Assert.Equal(1, deleted.GetProperty("keyEpoch").GetInt32());
        var stored = await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == sent.MessageIdOf()));
        Assert.Null(stored.Ciphertext);
        Assert.Equal(1, stored.KeyEpoch);
        var history = await client.GetHistoryAsync(group);
        Assert.Equal(JsonValueKind.Null, history.Single().GetProperty("ciphertext").ValueKind);
    }

    // ##### What the server insists on #####

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_message_needs_a_client_message_id(string? clientMessageId)
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);

        var response = await PostRawAsync(client, group, new { ciphertext = FakeMessage.Ciphertext(1, "x"), clientMessageId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await MessageCountAsync(group));
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("empty")]
    [InlineData("not base64url")]
    [InlineData("padded")]
    [InlineData("too short")]
    [InlineData("wrong version")]
    [InlineData("wrong suite")]
    [InlineData("no epoch")]
    [InlineData("too large")]
    public async Task A_blob_that_is_not_a_message_is_refused_and_stores_nothing(string kind)
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        var blob = FakeMessage.Blob(1, "x");
        string? ciphertext = kind switch
        {
            "nothing" => null,
            "empty" => "",
            "not base64url" => "not base64url!",
            "padded" => KeyFormats.Encode(blob) + "=",
            "too short" => KeyFormats.Encode(FakeMessage.Blob(1, "")[..97]),
            "wrong version" => KeyFormats.Encode([.. new byte[] { 2 }, .. blob[1..]]),
            "wrong suite" => KeyFormats.Encode([.. blob[..1], .. new byte[] { 2 }, .. blob[2..]]),
            "no epoch" => KeyFormats.Encode(FakeMessage.Blob(0, "x")),
            _ => KeyFormats.Encode(FakeMessage.Blob(1, new string('x', 32768))),
        };

        var response = await PostRawAsync(client, group, new { ciphertext, clientMessageId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await MessageCountAsync(group));
    }

    [Fact]
    public async Task The_largest_message_the_formats_allow_is_stored()
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);

        var response = await PostRawAsync(client, group, new
        {
            ciphertext = KeyFormats.Encode(FakeMessage.Blob(1, new string('x', 32768 - 98))),
            clientMessageId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ##### The epoch the conversation is in #####

    [Fact]
    public async Task A_message_sealed_under_an_epoch_that_is_not_the_current_one_is_refused_as_out_of_date_and_stores_nothing()
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        await client.AddMembersAsync(group, (await _factory.RegisterUserAsync("extra")).Id);
        var before = await UpdatedAtAsync(group);

        var older = await client.PostMessageAsync(group, "sealed before the change", epoch: 1);
        var ahead = await client.PostMessageAsync(group, "sealed for an epoch that is not there", epoch: 3);

        foreach (var refused in new[] { older, ahead })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(KeyEpochCodes.Stale, (await refused.ReadProblemAsync()).Code);
        }

        Assert.Equal(0, await MessageCountAsync(group));
        Assert.Equal(before, await UpdatedAtAsync(group));
        Assert.Equal(HttpStatusCode.Created, (await client.PostMessageAsync(group, "sealed under the current one", epoch: 2)).StatusCode);
    }

    [Fact]
    public async Task A_conversation_that_has_no_epoch_yet_refuses_a_message_as_out_of_date()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var group = await _factory.AddLegacyGroupAsync(owner.Id, "From before keys", member.Id);
        using var client = _factory.CreateClientFor(owner);

        var response = await client.PostMessageAsync(group, "nothing to seal under", epoch: 1);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.Stale, (await response.ReadProblemAsync()).Code);
        Assert.Equal(0, await MessageCountAsync(group));
    }

    [Fact]
    public async Task A_message_under_an_epoch_marked_as_due_by_a_removal_is_refused_until_the_epoch_is_replaced()
    {
        var (owner, _, other, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        await client.RemoveMemberResponseAsync(group, other.Id);

        var refused = await client.PostMessageAsync(group, "still under the old key", epoch: 1);
        var replaced = await client.StoreNextEpochAsync(group);
        var accepted = await client.PostMessageAsync(group, "under the new key", epoch: replaced);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(KeyEpochCodes.Due, (await refused.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(1, await MessageCountAsync(group));
    }

    [Fact]
    public async Task A_message_under_an_epoch_marked_as_due_by_a_leave_is_refused_until_the_epoch_is_replaced()
    {
        var (owner, member, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        using var leaving = _factory.CreateClientFor(member);
        await leaving.LeaveResponseAsync(group);

        var refused = await client.PostMessageAsync(group, "still under the old key", epoch: 1);

        Assert.Equal(KeyEpochCodes.Due, (await refused.ReadProblemAsync()).Code);
        await client.StoreNextEpochAsync(group);
        Assert.Equal(HttpStatusCode.Created, (await client.PostMessageAsync(group, "under the new key", epoch: 2)).StatusCode);
    }

    // A removal that has not committed yet holds the conversation's row. A send meant for the epoch it is about to mark as due must
    // wait for it and be refused when it commits: a read made before the send's insert would have seen the old state and let the
    // message in under a key that the person removed still holds.
    [Fact]
    public async Task A_send_that_waits_for_a_removal_is_refused_when_the_removal_commits()
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var removal = await db.Database.BeginTransactionAsync();
        await db.Conversations.Where(c => c.Id == group).ExecuteUpdateAsync(s => s.SetProperty(c => c.KeyRotationRequired, true));

        var send = client.PostMessageAsync(group, "in the way", epoch: 1);
        await Task.Delay(600);
        Assert.False(send.IsCompleted, "The send did not wait for the removal that holds the conversation.");
        await removal.CommitAsync();
        var response = await send;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.Due, (await response.ReadProblemAsync()).Code);
        Assert.Equal(0, await MessageCountAsync(group));
    }

    [Fact]
    public async Task A_refusal_for_the_epoch_leaves_no_files_behind_and_the_upload_can_be_used_again()
    {
        var (owner, _, other, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        var key = await client.UploadFileAsync(group, [1, 2, 3, 4], "image/png", "photo.png");
        await client.RemoveMemberResponseAsync(group, other.Id);

        var refused = await client.PostMessageWithFilesAsync(group, "with a file", [(key, "photo.png")]);
        var epoch = await client.StoreNextEpochAsync(group);
        var again = await client.PostMessageWithFilesAsync(group, "with a file", [(key, "photo.png")]);

        Assert.Equal(KeyEpochCodes.Due, (await refused.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(epoch, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("keyEpoch").GetInt32());
    }

    // ##### Who a message names #####

    [Fact]
    public async Task A_message_notifies_the_members_it_names_and_nobody_else()
    {
        var (owner, member, other, group) = await GroupAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(owner);
        var named = new[] { member.Id, outsider.Id, owner.Id, member.Id };

        var sent = await client.SendMessageAsync(group, "@member", mentions: named);

        var mention = Assert.Single(await MentionsOfAsync(member));
        Assert.Equal("Mention", mention.GetProperty("type").GetString());
        Assert.Equal(owner.Id, mention.GetProperty("actorId").GetGuid());
        Assert.Equal(sent.MessageIdOf(), mention.GetProperty("messageId").GetGuid());
        Assert.Equal(sent.GetProperty("ciphertext").GetString(), mention.GetProperty("message").GetProperty("ciphertext").GetString());
        Assert.Equal(1, mention.GetProperty("message").GetProperty("keyEpoch").GetInt32());
        Assert.Equal(sent.GetProperty("clientMessageId").GetGuid(), mention.GetProperty("message").GetProperty("clientMessageId").GetGuid());
        Assert.Equal(0, mention.GetProperty("message").GetProperty("editCount").GetInt32());
        foreach (var person in new[] { other, outsider, owner })
            Assert.Empty(await MentionsOfAsync(person));

        var stored = await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == sent.MessageIdOf()));
        Assert.Equal([member.Id], stored.MentionedUserIds);
    }

    [Fact]
    public async Task A_member_who_left_and_an_account_that_was_deactivated_are_not_notified()
    {
        var (owner, member, other, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        using var leaving = _factory.CreateClientFor(member);
        await leaving.LeaveResponseAsync(group);
        await client.StoreNextEpochAsync(group);
        await _factory.DeactivateAsync(other);

        var sent = await client.SendMessageAsync(group, "@member @other", epoch: 2, mentions: [member.Id, other.Id]);

        foreach (var person in new[] { member, other })
            Assert.Empty(await MentionsOfAsync(person));

        Assert.Empty((await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == sent.MessageIdOf()))).MentionedUserIds);
    }

    [Fact]
    public async Task A_message_that_names_nobody_makes_no_notification()
    {
        var (owner, member, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);

        await client.SendMessageAsync(group, "hello all");

        Assert.Empty(await MentionsOfAsync(member));
    }

    [Fact]
    public async Task At_most_twenty_people_can_be_named_and_an_empty_id_is_not_one_of_them()
    {
        var (owner, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        var twenty = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();

        var allowed = await client.PostMessageAsync(group, "twenty", mentions: twenty);
        var tooMany = await client.PostMessageAsync(group, "twenty one", mentions: [.. twenty, Guid.NewGuid()]);
        var blank = await client.PostMessageAsync(group, "blank", mentions: [Guid.Empty]);

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }
}
