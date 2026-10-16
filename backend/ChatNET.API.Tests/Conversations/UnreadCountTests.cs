using System.Net.Http.Json;
using ChatNET.API.Common.Time;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Conversations;

// The unread count is what the conversation list shows next to a conversation: how many messages
// from other people arrived after the member last read it. It is computed by the list query from
// the member's read position, never stored as a number that could drift.
[Collection(IntegrationTestCollection.Name)]
public class UnreadCountTests
{
    // The count stops here: the client shows "99+" and nothing needs the exact number past it, so
    // the query never reads more than this many rows per conversation.
    private const int Cap = 100;

    private readonly ChatApiFactory _factory;

    public UnreadCountTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### What counts #####

    [Fact]
    public async Task A_conversation_without_messages_has_nothing_unread()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        Assert.Equal(0, await client.UnreadInListAsync(conversation));
    }

    [Fact]
    public async Task Messages_from_the_other_person_are_unread_and_ones_you_sent_are_not()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        await bobClient.SendMessageAsync(conversation, "one");
        await bobClient.SendMessageAsync(conversation, "two");

        Assert.Equal(2, await aliceClient.UnreadInListAsync(conversation));
        Assert.Equal(0, await bobClient.UnreadInListAsync(conversation));
    }

    // Sending moves the sender's read position, but messages stored before that rule existed (or by
    // any other path) did not, so the count must not rely on it to leave a member's own messages out.
    [Fact]
    public async Task Own_messages_stored_without_moving_the_read_position_are_still_not_counted()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, alice.Id, 5, DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(1), "alice");
        await _factory.AddMessagesAsync(conversation, bob.Id, 3, DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(1), "bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        Assert.Equal(3, await aliceClient.UnreadInListAsync(conversation));
        Assert.Equal(5, await bobClient.UnreadInListAsync(conversation));
    }

    [Fact]
    public async Task Each_member_of_a_group_has_their_own_count()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        await aliceClient.SendMessageAsync(group, "from alice 1");
        await aliceClient.SendMessageAsync(group, "from alice 2");
        var fromBob = await bobClient.SendMessageAsync(group, "from bob");

        // Alice sees Bob's one; Bob sees Alice's two and none of his own; Carol sees all three.
        Assert.Equal(1, await aliceClient.UnreadInListAsync(group));
        Assert.Equal(0, await bobClient.UnreadInListAsync(group));
        Assert.Equal(3, await carolClient.UnreadInListAsync(group));

        // One member reading does not change what the others have left to read.
        await carolClient.MarkReadAsync(group, fromBob.MessageIdOf());
        Assert.Equal(0, await carolClient.UnreadInListAsync(group));
        Assert.Equal(1, await aliceClient.UnreadInListAsync(group));
    }

    // Joining a conversation does not make its earlier history unread; it is not even readable.
    [Fact]
    public async Task Only_messages_from_after_a_member_joined_count()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Late joiner", bob.Id, carol.Id)).IdOf();
        await _factory.BackdateJoinAsync(group);
        var joined = DateTime.UtcNow.AddMinutes(-30).TruncateToMicroseconds();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == carol.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, joined)));
        await _factory.AddMessageAsync(group, bob.Id, "before carol", joined.AddMinutes(-10));
        await _factory.AddMessageAsync(group, bob.Id, "just before", joined.AddTicks(-10));
        await _factory.AddMessageAsync(group, bob.Id, "exactly when", joined);
        await _factory.AddMessageAsync(group, bob.Id, "after carol", joined.AddMinutes(10));

        Assert.Equal(2, await carolClient.UnreadInListAsync(group));
        Assert.Equal(4, await aliceClient.UnreadInListAsync(group));
    }

    [Fact]
    public async Task The_count_stops_at_the_cap()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, Cap + 50, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);

        Assert.Equal(Cap, await client.UnreadInListAsync(conversation));
    }

    [Fact]
    public async Task Exactly_the_cap_is_reported_as_the_cap()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, Cap, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);

        Assert.Equal(Cap, await client.UnreadInListAsync(conversation));
    }

    [Fact]
    public async Task One_message_fewer_than_the_cap_is_counted_exactly()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        await _factory.AddMessagesAsync(conversation, bob.Id, Cap - 1, DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(10));
        using var client = _factory.CreateClientFor(alice);

        Assert.Equal(Cap - 1, await client.UnreadInListAsync(conversation));
    }

    // ##### Where the count appears #####

    [Fact]
    public async Task Every_view_of_a_conversation_carries_the_count()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var created = await aliceClient.CreateDirectAsync(bob.Id);
        var conversation = created.IdOf();
        await bobClient.SendMessageAsync(conversation, "hello");
        await bobClient.SendMessageAsync(conversation, "hello again");

        var single = await aliceClient.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/conversations/{conversation}");
        var again = await aliceClient.CreateDirectAsync(bob.Id);

        Assert.Equal(0, created.UnreadOf());
        Assert.Equal(2, single.UnreadOf());
        Assert.Equal(2, again.UnreadOf());
    }

    // ##### Sending counts as reading #####

    // A member who sends a message has seen the conversation up to that point. Besides being what
    // people expect, it keeps the query cheap: the member's own messages never pile up after their
    // read position for the count to step over.
    [Fact]
    public async Task Sending_a_message_moves_the_senders_read_position_to_it()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        await bobClient.SendMessageAsync(conversation, "are you there?");
        await bobClient.SendMessageAsync(conversation, "hello?");
        Assert.Equal(2, await aliceClient.UnreadInListAsync(conversation));

        var reply = await aliceClient.SendMessageAsync(conversation, "yes, sorry");

        Assert.Equal(0, await aliceClient.UnreadInListAsync(conversation));
        Assert.Equal(reply.GetProperty("createdAt").GetDateTime(), await _factory.LastReadAtAsync(conversation, alice.Id));
    }

    [Fact]
    public async Task Sending_does_not_mark_anything_read_for_the_other_members()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        await aliceClient.SendMessageAsync(conversation, "first");
        await aliceClient.SendMessageAsync(conversation, "second");

        Assert.Equal(2, await bobClient.UnreadInListAsync(conversation));
        Assert.Null(await _factory.LastReadAtAsync(conversation, bob.Id));
    }

    // ##### Helpers #####

    private async Task<(TestUser Alice, TestUser Bob, Guid ConversationId)> DirectChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(conversation);
        return (alice, bob, conversation);
    }
}
