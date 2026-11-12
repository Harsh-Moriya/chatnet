using System.Net;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Messaging;

// Every change of a message (a send, an edit, a delete) takes the next number of its conversation, in the transaction that makes the
// change. The numbers are what the changes feed is read by, so they must be consecutive, one per change, and never used up by a
// change that did not happen.
[Collection(IntegrationTestCollection.Name)]
public class ChangeSequenceTests
{
    private readonly ChatApiFactory _factory;

    public ChangeSequenceTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private Task<long> CounterAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Conversations.Where(c => c.Id == conversation).Select(c => c.ChangeSeq).SingleAsync());

    private Task<List<long>> NumbersAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == conversation).Select(m => m.ChangeSeq).OrderBy(n => n).ToListAsync());

    // ##### A send #####

    [Fact]
    public async Task A_conversation_starts_at_zero()
    {
        var (alice, bob, _) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        Assert.Equal(0L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task Sends_take_the_numbers_one_after_another_and_the_answer_says_which()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var first = await client.SendMessageAsync(conversation, "one");
        var second = await client.SendMessageAsync(conversation, "two");
        var third = await client.SendMessageAsync(conversation, "three");

        Assert.Equal([1L, 2L, 3L], new[] { first.ChangeSeqOf(), second.ChangeSeqOf(), third.ChangeSeqOf() });
        Assert.Equal(3L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task Every_conversation_counts_on_its_own()
    {
        var (alice, bob, one) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var other = (await client.CreateGroupAsync("Other", bob.Id)).IdOf();

        await client.SendMessageAsync(one, "a");
        await client.SendMessageAsync(one, "b");
        var inOther = await client.SendMessageAsync(other, "c");

        Assert.Equal(1L, inOther.ChangeSeqOf());
        Assert.Equal(2L, await CounterAsync(one));
        Assert.Equal(1L, await CounterAsync(other));
    }

    [Fact]
    public async Task Sends_made_at_the_same_moment_take_different_numbers_with_none_missing()
    {
        var (alice, bob, conversation) = await ChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        var sends = Enumerable.Range(0, 24)
            .Select(i => (i % 2 == 0 ? aliceClient : bobClient).SendMessageAsync(conversation, $"message {i}"))
            .ToList();
        await Task.WhenAll(sends);

        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), await NumbersAsync(conversation));
        Assert.Equal(24L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task A_send_that_is_answered_as_a_repeat_takes_no_number()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientId = Guid.NewGuid();
        await client.SendMessageAsync(conversation, "once", clientId);

        var again = await client.PostMessageAsync(conversation, "once", clientId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task A_send_refused_for_its_epoch_takes_no_number()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "first");

        var refused = await client.PostMessageAsync(conversation, "late", epoch: 7);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(1L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task A_send_refused_because_the_epoch_is_due_takes_no_number()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "first");
        await _factory.ExecuteDbAsync(db => db.Conversations
            .Where(c => c.Id == conversation)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.KeyRotationRequired, true)));

        var refused = await client.PostMessageAsync(conversation, "late");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(1L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task A_number_is_not_left_unused_when_a_send_is_refused_between_two_that_are_stored()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(conversation, "first");
        await client.PostMessageAsync(conversation, "refused", epoch: 7);
        var next = await client.SendMessageAsync(conversation, "second");

        Assert.Equal(2L, next.ChangeSeqOf());
    }

    // ##### An edit #####

    [Fact]
    public async Task An_edit_takes_the_next_number_and_the_message_carries_it()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.SendMessageAsync(conversation, "one");
        await client.SendMessageAsync(conversation, "two");

        var edited = await client.EditMessageAsync(conversation, first.MessageIdOf(), "one, edited");

        Assert.Equal(3L, edited.ChangeSeqOf());
        Assert.Equal(3L, await CounterAsync(conversation));
        Assert.Equal([2L, 3L], await NumbersAsync(conversation));
    }

    [Fact]
    public async Task An_edit_does_not_move_the_conversation_in_the_sidebar()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var sent = await client.SendMessageAsync(conversation, "one");
        var before = await _factory.QueryDbAsync(db => db.Conversations.Where(c => c.Id == conversation).Select(c => c.UpdatedAt).SingleAsync());

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "one, edited");

        Assert.Equal(before, await _factory.QueryDbAsync(db => db.Conversations.Where(c => c.Id == conversation).Select(c => c.UpdatedAt).SingleAsync()));
    }

    [Fact]
    public async Task Edits_made_at_the_same_moment_take_different_numbers_with_none_missing()
    {
        var (alice, bob, conversation) = await ChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var mine = await aliceClient.SendMessageAsync(conversation, "mine");
        var yours = await bobClient.SendMessageAsync(conversation, "yours");

        await Task.WhenAll(
            aliceClient.EditMessageAsync(conversation, mine.MessageIdOf(), "mine 1"),
            bobClient.EditMessageAsync(conversation, yours.MessageIdOf(), "yours 1"));

        Assert.Equal([3L, 4L], await NumbersAsync(conversation));
        Assert.Equal(4L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task An_edit_from_an_old_version_takes_no_number()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var sent = await client.SendMessageAsync(conversation, "one");
        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "two");

        var stale = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "three", editCount: 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task An_edit_of_a_deleted_message_takes_no_number()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var sent = await client.SendMessageAsync(conversation, "one");
        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        var refused = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "two", editCount: 1);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(2L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task An_edit_refused_for_its_epoch_takes_no_number()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var sent = await client.SendMessageAsync(conversation, "one");

        var refused = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "two", epoch: 7);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(1L, await CounterAsync(conversation));
    }

    // ##### A delete #####

    [Fact]
    public async Task A_delete_takes_the_next_number_and_the_message_carries_it()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.SendMessageAsync(conversation, "one");
        await client.SendMessageAsync(conversation, "two");

        var deleted = await client.DeleteMessageAsync(conversation, first.MessageIdOf());

        Assert.Equal(3L, deleted.ChangeSeqOf());
        Assert.Equal(3L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task Deleting_a_message_again_takes_no_number()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var sent = await client.SendMessageAsync(conversation, "one");
        var first = await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        var again = await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        Assert.Equal(2L, first.ChangeSeqOf());
        Assert.Equal(2L, again.ChangeSeqOf());
        Assert.Equal(2L, await CounterAsync(conversation));
    }

    [Fact]
    public async Task A_group_admin_deleting_a_message_is_numbered_like_any_other_change()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        using var ownerClient = _factory.CreateClientFor(owner);
        var group = (await ownerClient.CreateGroupAsync("Team", member.Id)).IdOf();
        using var memberClient = _factory.CreateClientFor(member);
        var sent = await memberClient.SendMessageAsync(group, "from a member");

        var deleted = await ownerClient.DeleteMessageAsync(group, sent.MessageIdOf());

        Assert.Equal(2L, deleted.ChangeSeqOf());
    }

    // ##### Where the number is shown #####

    [Fact]
    public async Task The_list_of_conversations_says_the_number_of_the_latest_change_of_each()
    {
        var (alice, bob, busy) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var quiet = (await client.CreateGroupAsync("Quiet", bob.Id)).IdOf();
        var first = await client.SendMessageAsync(busy, "one");
        await client.SendMessageAsync(busy, "two");
        await client.EditMessageAsync(busy, first.MessageIdOf(), "one, edited");

        var list = await client.ListConversationsAsync();

        Assert.Equal(3L, list.Single(c => c.IdOf() == busy).GetProperty("changeSeq").GetInt64());
        Assert.Equal(0L, list.Single(c => c.IdOf() == quiet).GetProperty("changeSeq").GetInt64());
    }

    [Fact]
    public async Task The_history_carries_the_number_of_each_message()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.SendMessageAsync(conversation, "one");
        await client.SendMessageAsync(conversation, "two");
        await client.EditMessageAsync(conversation, first.MessageIdOf(), "one, edited");

        var history = await client.GetHistoryAsync(conversation);

        Assert.Equal([3L, 2L], history.Select(m => m.ChangeSeqOf()).ToArray());
    }

    // ##### Together #####

    [Fact]
    public async Task The_counter_is_the_highest_number_a_message_holds_whatever_was_done()
    {
        var (alice, bob, conversation) = await ChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var one = await aliceClient.SendMessageAsync(conversation, "one");
        var two = await bobClient.SendMessageAsync(conversation, "two");
        await aliceClient.EditMessageAsync(conversation, one.MessageIdOf(), "one again");
        await bobClient.DeleteMessageAsync(conversation, two.MessageIdOf());
        await aliceClient.PostMessageAsync(conversation, "never", epoch: 7);
        await aliceClient.SendMessageAsync(conversation, "three");

        var numbers = await NumbersAsync(conversation);

        Assert.Equal(await CounterAsync(conversation), numbers[^1]);
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public async Task A_message_stored_by_a_path_that_forgot_the_number_is_refused_by_the_database()
    {
        var (alice, _, conversation) = await ChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.SendMessageAsync(conversation, "one");

        var clash = await Assert.ThrowsAsync<DbUpdateException>(() => _factory.ExecuteDbAsync(async db =>
        {
            db.Messages.Add(new ChatNET.API.Messaging.Models.Message
            {
                ConversationId = conversation,
                SenderId = alice.Id,
                Ciphertext = FakeMessage.Blob(1, "forgot"),
                KeyEpoch = 1,
                CreatedAt = DateTime.UtcNow,
                ClientMessageId = Guid.NewGuid(),
                ChangeSeq = 1,
            });
            await db.SaveChangesAsync();
        }));

        Assert.Contains("IX_Messages_ConversationId_ChangeSeq", clash.InnerException!.Message);
    }

    // A direct conversation between two new people.
    private async Task<(TestUser Alice, TestUser Bob, Guid Conversation)> ChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        return (alice, bob, conversation);
    }
}
