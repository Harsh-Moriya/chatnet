using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Conversations;

[Collection(IntegrationTestCollection.Name)]
public class CreateGroupConversationTests
{
    private readonly ChatApiFactory _factory;

    public CreateGroupConversationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Creates_a_group_that_every_member_sees_under_the_group_name()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        var outsider = await _factory.RegisterUserAsync("dave");
        using var aliceClient = _factory.CreateClientFor(alice);

        var response = await aliceClient.PostGroupAsync("Team", bob.Id, carol.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Group", created.GetProperty("type").GetString());
        Assert.Equal("Team", created.GetProperty("name").GetString());

        foreach (var member in new[] { alice, bob, carol })
        {
            using var client = _factory.CreateClientFor(member);
            var seen = (await client.ListConversationsAsync()).Single(c => c.IdOf() == created.IdOf());
            Assert.Equal("Team", seen.GetProperty("name").GetString());
        }

        using var outsiderClient = _factory.CreateClientFor(outsider);
        Assert.Empty(await outsiderClient.ListConversationsAsync());
    }

    [Fact]
    public async Task Location_header_names_the_new_conversation()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostGroupAsync("Team", bob.Id);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Only the header's content is checked: there is no GET for a single conversation yet,
        // so following it would be a 404.
        Assert.Equal($"/api/conversations/{created.IdOf()}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task The_creator_is_the_owner_and_everyone_else_a_regular_member()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);

        var id = (await client.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        var (conversation, members) = await _factory.QueryDbAsync(async db =>
            (await db.Conversations.SingleAsync(c => c.Id == id),
             await db.ConversationMembers.Where(m => m.ConversationId == id).ToListAsync()));

        Assert.Equal(ConversationType.Group, conversation.Type);
        Assert.Null(conversation.DirectKey);
        Assert.Equal(alice.Id, conversation.CreatedById);
        Assert.Equal(3, members.Count);
        Assert.Equal(MemberRole.Owner, members.Single(m => m.UserId == alice.Id).Role);
        Assert.All(members.Where(m => m.UserId != alice.Id), m => Assert.Equal(MemberRole.Member, m.Role));
        Assert.All(members, m => Assert.Null(m.LeftAt));
    }

    [Fact]
    public async Task Listing_yourself_or_a_member_twice_does_not_create_duplicate_memberships()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var id = (await client.CreateGroupAsync("Team", alice.Id, bob.Id, bob.Id)).IdOf();

        var memberIds = await _factory.QueryDbAsync(db =>
            db.ConversationMembers.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync());
        Assert.Equivalent(new[] { alice.Id, bob.Id }, memberIds);
    }

    [Fact]
    public async Task A_group_does_not_interfere_with_the_direct_conversation_of_the_same_two_people()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var group = await client.CreateGroupAsync("Just us", bob.Id);
        var direct = await client.CreateDirectAsync(bob.Id);

        Assert.NotEqual(group.IdOf(), direct.IdOf());
        Assert.Equal(2, (await client.ListConversationsAsync()).Count);
    }

    [Fact]
    public async Task Group_names_are_not_unique()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var first = await client.CreateGroupAsync("Same name", bob.Id);
        var second = await client.CreateGroupAsync("Same name", bob.Id);

        Assert.NotEqual(first.IdOf(), second.IdOf());
    }

    [Fact]
    public async Task A_group_with_only_the_creator_is_a_409_and_creates_nothing()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostGroupAsync("Lonely", alice.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("at least one other member", (await response.ReadProblemAsync()).Detail);
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task An_empty_member_list_is_a_400()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostGroupAsync("Nobody");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("between 1 and 100", (await response.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_missing_member_list_is_a_400()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostAsJsonAsync("/api/conversations/group", new { name = "No list" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task More_than_one_hundred_members_is_a_400()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);
        var tooMany = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray();

        var response = await client.PostGroupAsync("Crowd", tooMany);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_member_is_a_404_naming_the_id_and_nothing_is_created()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var ghost = Guid.NewGuid();

        var response = await client.PostGroupAsync("Haunted", bob.Id, ghost);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ghost.ToString(), (await response.ReadProblemAsync()).Detail);
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task A_deactivated_member_is_a_404()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await _factory.DeactivateAsync(bob);
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostGroupAsync("Gone", bob.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_a_400(string name)
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostGroupAsync(name, bob.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_name_is_a_400()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostAsJsonAsync("/api/conversations/group", new { memberIds = new[] { bob.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_name_may_be_one_hundred_characters_but_not_one_hundred_and_one()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var longest = await client.PostGroupAsync(new string('n', 100), bob.Id);
        var tooLong = await client.PostGroupAsync(new string('n', 101), bob.Id);

        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Surrounding_spaces_are_trimmed_from_the_name()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var created = await client.CreateGroupAsync("  Team  ", bob.Id);

        Assert.Equal("Team", created.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Requires_authentication()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostGroupAsync("Anon", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
