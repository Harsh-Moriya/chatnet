using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Conversations;

[Collection(IntegrationTestCollection.Name)]
public class GetConversationTests
{
    private const string NotAMember = "You are not a member of this conversation.";

    private readonly ChatApiFactory _factory;

    public GetConversationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_member_gets_the_conversation_as_they_see_it()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var id = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();

        var forAlice = await aliceClient.GetFromJsonAsync<JsonElement>($"/api/conversations/{id}");
        var forBob = await bobClient.GetFromJsonAsync<JsonElement>($"/api/conversations/{id}");

        Assert.Equal(id, forAlice.IdOf());
        Assert.Equal("Direct", forAlice.GetProperty("type").GetString());
        // A direct chat is named after the other person, so each side sees a different name.
        Assert.Equal(bob.DisplayName, forAlice.GetProperty("name").GetString());
        Assert.Equal(alice.DisplayName, forBob.GetProperty("name").GetString());
    }

    [Fact]
    public async Task It_matches_the_entry_in_the_list_including_the_last_message()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var id = (await client.CreateGroupAsync("Team", bob.Id)).IdOf();
        await client.SendMessageAsync(id, "Latest");

        var single = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{id}");
        var listed = (await client.ListConversationsAsync()).Single(c => c.IdOf() == id);

        Assert.Equal(listed.GetRawText(), single.GetRawText());
        Assert.Equal("Latest", single.GetProperty("lastMessage").GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_location_header_of_a_new_group_leads_to_it()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var created = await client.PostGroupAsync("Team", bob.Id);
        var followed = await client.GetAsync(created.Headers.Location);

        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        var body = await followed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((await created.Content.ReadFromJsonAsync<JsonElement>()).IdOf(), body.IdOf());
    }

    [Fact]
    public async Task A_non_member_is_refused()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var intruderClient = _factory.CreateClientFor(intruder);
        var id = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();

        var response = await intruderClient.GetAsync($"/api/conversations/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(NotAMember, (await response.ReadProblemAsync()).Detail);
    }

    // The same rule as sending and history: IDs cannot be probed.
    [Fact]
    public async Task An_unknown_conversation_gets_the_same_refusal()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync($"/api/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(NotAMember, (await response.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_member_who_has_left_is_refused()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var id = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == id && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await bobClient.GetAsync($"/api/conversations/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_does_not_match_the_route()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync("/api/conversations/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
