using System.Net;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Conversations;

// The members of a conversation with how far each has read. The client needs it to show who has
// seen a message, and to name them.
[Collection(IntegrationTestCollection.Name)]
public class ConversationMembersTests
{
    private readonly ChatApiFactory _factory;

    public ConversationMembersTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_group_lists_every_member_with_their_role_and_name()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(bob);
        using var creator = _factory.CreateClientFor(alice);
        var group = (await creator.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        var members = await client.GetMembersAsync(group);

        Assert.Equal(3, members.Count);
        var byId = members.ToDictionary(m => m.GetProperty("userId").GetGuid());
        Assert.Equal("Owner", byId[alice.Id].GetProperty("role").GetString());
        Assert.Equal("Member", byId[bob.Id].GetProperty("role").GetString());
        Assert.Equal(carol.DisplayName, byId[carol.Id].GetProperty("displayName").GetString());
    }

    // What a message must say to mention someone: the autocomplete in the composer writes this name.
    [Fact]
    public async Task Each_member_carries_the_user_name_a_mention_uses()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        var members = await client.GetMembersAsync(conversation);

        var byId = members.ToDictionary(m => m.GetProperty("userId").GetGuid());
        Assert.Equal(alice.UserName, byId[alice.Id].GetProperty("userName").GetString());
        Assert.Equal(bob.UserName, byId[bob.Id].GetProperty("userName").GetString());
    }

    [Fact]
    public async Task A_direct_chat_lists_both_people()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        var members = await client.GetMembersAsync(conversation);

        Assert.Equivalent(new[] { alice.Id, bob.Id }, members.Select(m => m.GetProperty("userId").GetGuid()));
    }

    [Fact]
    public async Task Each_member_has_exactly_the_documented_fields()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        var member = (await client.GetMembersAsync(conversation))[0];

        Assert.Equal(
            new[] { "avatarUrl", "displayName", "lastReadAt", "role", "userId", "userName" },
            member.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task The_read_position_is_null_until_a_member_reads_or_sends_and_then_follows_it()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var conversation = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        var before = await aliceClient.GetMembersAsync(conversation);
        var sent = await bobClient.SendMessageAsync(conversation, "hello");
        await aliceClient.MarkReadAsync(conversation, sent.MessageIdOf());

        var after = await aliceClient.GetMembersAsync(conversation);

        Assert.All(before, m => Assert.Equal(JsonValueKind.Null, m.GetProperty("lastReadAt").ValueKind));
        var sentAt = sent.GetProperty("createdAt").GetDateTime();
        Assert.All(after, m => Assert.Equal(sentAt, m.GetProperty("lastReadAt").GetDateTime()));
    }

    [Fact]
    public async Task A_member_who_has_left_is_not_listed()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Leaving", bob.Id, carol.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == carol.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var members = await client.GetMembersAsync(group);

        Assert.DoesNotContain(members, m => m.GetProperty("userId").GetGuid() == carol.Id);
        Assert.Equal(2, members.Count);
    }

    [Fact]
    public async Task Only_a_member_may_ask_and_an_unknown_conversation_gets_the_same_refusal()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var intruderClient = _factory.CreateClientFor(intruder);
        var conversation = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();

        var existing = await intruderClient.GetMembersResponseAsync(conversation);
        var unknown = await intruderClient.GetMembersResponseAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, existing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal((await existing.ReadProblemAsync()).Detail, (await unknown.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_member_who_has_left_may_no_longer_ask()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var group = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await bobClient.GetMembersResponseAsync(group);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Members_require_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetMembersResponseAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
