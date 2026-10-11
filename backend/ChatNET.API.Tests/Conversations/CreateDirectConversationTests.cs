using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Conversations;

[Collection(IntegrationTestCollection.Name)]
public class CreateDirectConversationTests
{
    private readonly ChatApiFactory _factory;

    public CreateDirectConversationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Each_participant_sees_the_conversation_named_after_the_other_person()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        var created = await aliceClient.CreateDirectAsync(bob.Id);

        Assert.Equal("Direct", created.GetProperty("type").GetString());
        Assert.Equal(bob.DisplayName, created.GetProperty("name").GetString());

        var seenByBob = (await bobClient.ListConversationsAsync()).Single(c => c.IdOf() == created.IdOf());
        Assert.Equal(alice.DisplayName, seenByBob.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Repeating_the_call_or_calling_from_the_other_side_returns_the_same_conversation()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        var first = await aliceClient.CreateDirectAsync(bob.Id);
        var again = await aliceClient.CreateDirectAsync(bob.Id);
        var fromBob = await bobClient.CreateDirectAsync(alice.Id);

        Assert.Equal(first.IdOf(), again.IdOf());
        Assert.Equal(first.IdOf(), fromBob.IdOf());
        Assert.Single(await aliceClient.ListConversationsAsync());
        Assert.Single(await bobClient.ListConversationsAsync());
    }

    [Fact]
    public async Task Different_pairs_get_different_conversations()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);

        var withBob = await aliceClient.CreateDirectAsync(bob.Id);
        var withCarol = await aliceClient.CreateDirectAsync(carol.Id);

        Assert.NotEqual(withBob.IdOf(), withCarol.IdOf());
    }

    [Fact]
    public async Task A_third_user_cannot_see_the_conversation()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);

        await aliceClient.CreateDirectAsync(bob.Id);

        Assert.Empty(await carolClient.ListConversationsAsync());
    }

    // The creator must always be the authenticated user. A client that adds a "userId" of its own
    // to the body, hoping to create a chat on someone else's behalf, is ignored.
    [Fact]
    public async Task The_creator_comes_from_the_token_never_from_the_request_body()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);

        var response = await aliceClient.PostAsJsonAsync("/api/conversations/direct",
            new { otherUserId = bob.Id, userId = carol.Id, createdById = carol.Id });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).IdOf();

        Assert.Empty(await carolClient.ListConversationsAsync());
        var creator = await _factory.QueryDbAsync(db =>
            db.Conversations.Where(c => c.Id == id).Select(c => c.CreatedById).SingleAsync());
        Assert.Equal(alice.Id, creator);
    }

    [Fact]
    public async Task Stores_both_participants_as_active_members_with_the_direct_key()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);

        var id = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();

        var (conversation, members) = await _factory.QueryDbAsync(async db =>
            (await db.Conversations.SingleAsync(c => c.Id == id),
             await db.ConversationMembers.Where(m => m.ConversationId == id).ToListAsync()));

        Assert.Equal(ConversationType.Direct, conversation.Type);
        Assert.Equal(DirectKey.For(alice.Id, bob.Id), conversation.DirectKey);
        Assert.Null(conversation.Name);
        Assert.Equal(2, members.Count);
        Assert.Equivalent(new[] { alice.Id, bob.Id }, members.Select(m => m.UserId));
        Assert.All(members, m =>
        {
            Assert.Null(m.LeftAt);
            Assert.Equal(MemberRole.Member, m.Role);
        });
    }

    [Fact]
    public async Task Cannot_start_a_conversation_with_yourself()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostDirectAsync(alice.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("yourself", (await response.ReadProblemAsync()).Detail);
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task An_empty_user_id_is_rejected()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostDirectAsync(Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_user_is_a_404_and_creates_nothing()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);
        var ghost = Guid.NewGuid();

        var response = await client.PostDirectAsync(ghost);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ghost.ToString(), (await response.ReadProblemAsync()).Detail);
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task A_deactivated_user_cannot_be_messaged()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await _factory.DeactivateAsync(bob);
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostDirectAsync(bob.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_response_has_exactly_the_documented_fields_and_a_utc_timestamp()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var created = await client.CreateDirectAsync(bob.Id);

        var fields = created.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "avatarUrl", "id", "lastMessage", "name", "type", "updatedAt" }, fields);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastMessage").ValueKind);
        Assert.EndsWith("Z", created.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task Requires_authentication()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostDirectAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
