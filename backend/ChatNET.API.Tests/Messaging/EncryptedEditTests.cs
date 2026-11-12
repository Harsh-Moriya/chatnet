using System.Net;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Keys;
using ChatNET.API.Messaging;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// Editing a message the server cannot read: the new text is sealed for the next edit count under the epoch the conversation is in, and
// the server settles the races (two edits, an edit and a removal) and who is notified, without ever seeing a word.
[Collection(IntegrationTestCollection.Name)]
public class EncryptedEditTests
{
    private readonly ChatApiFactory _factory;

    public EncryptedEditTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // What the tests read back of a message, without the rest of the row. The blob and the names are compared by what they hold.
    private sealed record StoredMessage(byte[]? Ciphertext, int KeyEpoch, int EditCount, DateTime? EditedAt, Guid[] MentionedUserIds)
    {
        public bool Equals(StoredMessage? other) =>
            other is not null && KeyEpoch == other.KeyEpoch && EditCount == other.EditCount && EditedAt == other.EditedAt
            && Ciphertext.AsSpan().SequenceEqual(other.Ciphertext) && MentionedUserIds.AsSpan().SequenceEqual(other.MentionedUserIds);

        public override int GetHashCode() => HashCode.Combine(KeyEpoch, EditCount, EditedAt);
    }

    private async Task<(TestUser Owner, TestUser Member, TestUser Other, Guid Group, Guid MessageId)> SentAsync(IEnumerable<Guid>? mentions = null)
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var other = await _factory.RegisterUserAsync("other");
        using var client = _factory.CreateClientFor(owner);
        var group = (await client.CreateGroupAsync("Team", member.Id, other.Id)).IdOf();
        var sent = await client.SendMessageAsync(group, "first draft", mentions: mentions);
        return (owner, member, other, group, sent.MessageIdOf());
    }

    private Task<StoredMessage> StoredAsync(Guid messageId) =>
        _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.Id == messageId)
            .Select(m => new StoredMessage(m.Ciphertext, m.KeyEpoch, m.EditCount, m.EditedAt, m.MentionedUserIds))
            .SingleAsync());

    // The notifications of a person that are about a mention: creating a group tells its members too, which is not what these look at.
    private async Task<List<JsonElement>> MentionsOfAsync(TestUser person)
    {
        using var client = _factory.CreateClientFor(person);
        return (await client.GetNotificationsAsync()).ItemsOf().Where(n => n.GetProperty("type").GetString() == "Mention").ToList();
    }

    // ##### What an edit stores #####

    [Fact]
    public async Task An_edit_stores_the_new_blob_counts_the_edit_and_keeps_the_epoch_it_was_sealed_in()
    {
        var (owner, _, _, group, id) = await SentAsync();
        using var client = _factory.CreateClientFor(owner);
        await client.AddMembersAsync(group, (await _factory.RegisterUserAsync("extra")).Id);

        var edited = await client.EditMessageAsync(group, id, "second draft", editCount: 1, epoch: 2);

        Assert.Equal(FakeMessage.Ciphertext(2, "second draft"), edited.GetProperty("ciphertext").GetString());
        Assert.Equal(2, edited.GetProperty("keyEpoch").GetInt32());
        Assert.Equal(1, edited.GetProperty("editCount").GetInt32());
        var stored = await StoredAsync(id);
        Assert.Equal(FakeMessage.Blob(2, "second draft"), stored.Ciphertext);
        Assert.Equal(2, stored.KeyEpoch);
        Assert.Equal(1, stored.EditCount);
        Assert.NotNull(stored.EditedAt);
    }

    // ##### The edit count #####

    // The word a client looks for in the problem details of an edit made from an older version: a change here is a change to the API.
    [Fact]
    public void An_edit_made_from_an_older_version_is_known_by_the_word_clients_look_for()
    {
        Assert.Equal("edit-count-stale", MessageCodes.EditCountStale);
    }

    [Fact]
    public async Task An_edit_must_be_the_next_one_after_the_count_the_message_has()
    {
        var (owner, _, _, group, id) = await SentAsync();
        using var client = _factory.CreateClientFor(owner);

        var skipped = await client.PatchMessageAsync(group, id, "skips one", editCount: 2);
        var first = await client.PatchMessageAsync(group, id, "first edit", editCount: 1);
        var repeated = await client.PatchMessageAsync(group, id, "from the old version", editCount: 1);
        var second = await client.PatchMessageAsync(group, id, "second edit", editCount: 2);

        Assert.Equal(HttpStatusCode.Conflict, skipped.StatusCode);
        Assert.Equal(MessageCodes.EditCountStale, (await skipped.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal(MessageCodes.EditCountStale, (await repeated.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await StoredAsync(id)).EditCount);
    }

    [Fact]
    public async Task A_refused_edit_changes_nothing()
    {
        var (owner, _, _, group, id) = await SentAsync();
        using var client = _factory.CreateClientFor(owner);
        var before = await StoredAsync(id);

        await client.PatchMessageAsync(group, id, "never stored", editCount: 5);

        Assert.Equal(before, await StoredAsync(id));
    }

    [Fact]
    public async Task Two_edits_made_from_the_same_version_store_one_and_the_other_is_told_to_look_again()
    {
        var (owner, _, _, group, id) = await SentAsync();
        using var one = _factory.CreateClientFor(owner);
        using var two = _factory.CreateClientFor(owner);

        var responses = await Task.WhenAll(
            one.PatchMessageAsync(group, id, "from the first tab", editCount: 1),
            two.PatchMessageAsync(group, id, "from the second tab", editCount: 1));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var refused = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(MessageCodes.EditCountStale, (await refused.ReadProblemAsync()).Code);
        var stored = await StoredAsync(id);
        Assert.Equal(1, stored.EditCount);
        Assert.Contains(stored.Ciphertext, new[] { FakeMessage.Blob(1, "from the first tab"), FakeMessage.Blob(1, "from the second tab") });
    }

    // ##### The epoch the conversation is in #####

    [Fact]
    public async Task An_edit_sealed_under_an_epoch_that_is_not_the_current_one_is_refused_as_out_of_date()
    {
        var (owner, _, _, group, id) = await SentAsync();
        using var client = _factory.CreateClientFor(owner);
        await client.AddMembersAsync(group, (await _factory.RegisterUserAsync("extra")).Id);
        var before = await StoredAsync(id);

        var older = await client.PatchMessageAsync(group, id, "under the old epoch", editCount: 1, epoch: 1);
        var ahead = await client.PatchMessageAsync(group, id, "under one that is not there", editCount: 1, epoch: 3);

        foreach (var refused in new[] { older, ahead })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(KeyEpochCodes.Stale, (await refused.ReadProblemAsync()).Code);
        }

        Assert.Equal(before, await StoredAsync(id));
    }

    [Fact]
    public async Task An_edit_under_an_epoch_marked_as_due_is_refused_until_the_epoch_is_replaced()
    {
        var (owner, _, other, group, id) = await SentAsync();
        using var client = _factory.CreateClientFor(owner);
        await client.RemoveMemberResponseAsync(group, other.Id);

        var refused = await client.PatchMessageAsync(group, id, "under the old key", editCount: 1, epoch: 1);
        var replaced = await client.StoreNextEpochAsync(group);
        var accepted = await client.PatchMessageAsync(group, id, "under the new key", editCount: 1, epoch: replaced);

        Assert.Equal(KeyEpochCodes.Due, (await refused.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    // A removal that has not committed yet holds the conversation's row, and an edit reads the conversation's keys under FOR SHARE: it
    // waits, and is refused when the removal commits, instead of storing text under a key the person removed still holds.
    [Fact]
    public async Task An_edit_that_waits_for_a_removal_is_refused_when_the_removal_commits()
    {
        var (owner, _, _, group, id) = await SentAsync();
        using var client = _factory.CreateClientFor(owner);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var removal = await db.Database.BeginTransactionAsync();
        await db.Conversations.Where(c => c.Id == group).ExecuteUpdateAsync(s => s.SetProperty(c => c.KeyRotationRequired, true));

        var edit = client.PatchMessageAsync(group, id, "in the way", editCount: 1, epoch: 1);
        await Task.Delay(600);
        Assert.False(edit.IsCompleted, "The edit did not wait for the removal that holds the conversation.");
        await removal.CommitAsync();
        var response = await edit;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.Due, (await response.ReadProblemAsync()).Code);
        Assert.Equal(0, (await StoredAsync(id)).EditCount);
    }

    // ##### Who an edit names #####

    [Fact]
    public async Task An_edit_notifies_the_people_it_names_who_the_message_did_not_name_before()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var other = await _factory.RegisterUserAsync("other");
        using var client = _factory.CreateClientFor(owner);
        var group = (await client.CreateGroupAsync("Team", member.Id, other.Id)).IdOf();
        var id = (await client.SendMessageAsync(group, "@member", mentions: [member.Id])).MessageIdOf();

        await client.EditMessageAsync(group, id, "@member and @other", mentions: [member.Id, other.Id]);

        Assert.Single(await MentionsOfAsync(member));
        var fresh = Assert.Single(await MentionsOfAsync(other));
        Assert.Equal(id, fresh.GetProperty("messageId").GetGuid());
        Assert.Equal(FakeMessage.Ciphertext(1, "@member and @other"), fresh.GetProperty("message").GetProperty("ciphertext").GetString());
        Assert.Equal(1, fresh.GetProperty("message").GetProperty("editCount").GetInt32());
        Assert.Equal([member.Id, other.Id], (await StoredAsync(id)).MentionedUserIds);
    }

    [Fact]
    public async Task A_person_who_dismissed_a_mention_is_not_notified_again_by_an_edit_that_still_names_them()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        using var client = _factory.CreateClientFor(owner);
        var group = (await client.CreateGroupAsync("Pair", member.Id)).IdOf();
        var id = (await client.SendMessageAsync(group, "@member", mentions: [member.Id])).MessageIdOf();
        using var memberClient = _factory.CreateClientFor(member);
        var mention = Assert.Single(await MentionsOfAsync(member));
        Assert.Equal(HttpStatusCode.NoContent, (await memberClient.DismissNotificationResponseAsync(mention.GetProperty("id").GetGuid())).StatusCode);

        await client.EditMessageAsync(group, id, "@member, fixed a typo", mentions: [member.Id]);

        Assert.Empty(await MentionsOfAsync(member));
    }

    [Fact]
    public async Task A_name_taken_out_keeps_its_notification_and_one_put_back_by_a_later_edit_is_notified_again()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        using var client = _factory.CreateClientFor(owner);
        var group = (await client.CreateGroupAsync("Pair", member.Id)).IdOf();
        var id = (await client.SendMessageAsync(group, "@member", mentions: [member.Id])).MessageIdOf();

        await client.EditMessageAsync(group, id, "nobody now", mentions: []);
        Assert.Single(await MentionsOfAsync(member));
        Assert.Empty((await StoredAsync(id)).MentionedUserIds);

        await client.EditMessageAsync(group, id, "@member again", mentions: [member.Id]);
        Assert.Equal(2, (await MentionsOfAsync(member)).Count);
    }

    [Fact]
    public async Task An_edit_does_not_notify_the_sender_a_stranger_or_someone_who_left()
    {
        var (owner, member, other, group, id) = await SentAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(owner);
        using var leaving = _factory.CreateClientFor(member);
        await leaving.LeaveResponseAsync(group);
        var epoch = await client.StoreNextEpochAsync(group);

        await client.EditMessageAsync(group, id, "names everyone", epoch: epoch, mentions: [owner.Id, outsider.Id, member.Id]);

        foreach (var person in new[] { owner, outsider, member, other })
            Assert.Empty(await MentionsOfAsync(person));
        Assert.Empty((await StoredAsync(id)).MentionedUserIds);
    }

    [Fact]
    public async Task An_edit_cannot_name_more_than_twenty_people()
    {
        var (owner, _, _, group, id) = await SentAsync();
        using var client = _factory.CreateClientFor(owner);
        var names = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();

        var response = await client.PatchMessageAsync(group, id, "too many", editCount: 1, mentions: names);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
