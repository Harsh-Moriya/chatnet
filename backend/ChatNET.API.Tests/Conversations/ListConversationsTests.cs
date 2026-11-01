using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Conversations;

// There is no endpoint to send a message yet, so tests that need messages insert them directly
// into the database. That is also the cleanest way to control timestamps and deleted state.
[Collection(IntegrationTestCollection.Name)]
public class ListConversationsTests
{
    private readonly ChatApiFactory _factory;

    public ListConversationsTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Lists_only_the_conversations_the_caller_belongs_to()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        var dave = await _factory.RegisterUserAsync("dave");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        using var daveClient = _factory.CreateClientFor(dave);

        var withBob = await aliceClient.CreateDirectAsync(bob.Id);
        var group = await aliceClient.CreateGroupAsync("Team", carol.Id);
        var carolDave = await carolClient.CreateDirectAsync(dave.Id);

        var aliceIds = (await aliceClient.ListConversationsAsync()).Select(c => c.IdOf()).ToList();
        var daveIds = (await daveClient.ListConversationsAsync()).Select(c => c.IdOf()).ToList();

        Assert.Equivalent(new[] { withBob.IdOf(), group.IdOf() }, aliceIds);
        Assert.Equivalent(new[] { carolDave.IdOf() }, daveIds);
    }

    [Fact]
    public async Task Most_recently_active_conversation_comes_first()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);

        var older = await client.CreateDirectAsync(bob.Id);
        var newer = await client.CreateGroupAsync("Newer", carol.Id);

        var before = (await client.ListConversationsAsync()).Select(c => c.IdOf()).ToList();
        Assert.Equal(new[] { newer.IdOf(), older.IdOf() }, before);

        // Activity in the older conversation moves it to the top.
        await SetUpdatedAtAsync(older.IdOf(), DateTime.UtcNow.AddMinutes(5));

        var after = (await client.ListConversationsAsync()).Select(c => c.IdOf()).ToList();
        Assert.Equal(new[] { older.IdOf(), newer.IdOf() }, after);
    }

    [Fact]
    public async Task Shows_the_latest_message_of_each_conversation_and_not_another_ones()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var withBob = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var withCarol = (await client.CreateDirectAsync(carol.Id)).IdOf();
        await _factory.BackdateJoinAsync(withBob);
        await _factory.BackdateJoinAsync(withCarol);

        var start = DateTime.UtcNow.AddMinutes(-10);
        await _factory.AddMessageAsync(withBob, alice.Id, "bob: first", start);
        await _factory.AddMessageAsync(withBob, bob.Id, "bob: latest", start.AddMinutes(2));
        await _factory.AddMessageAsync(withCarol, carol.Id, "carol: only", start.AddMinutes(1));

        var list = await client.ListConversationsAsync();

        var bobPreview = list.Single(c => c.IdOf() == withBob).GetProperty("lastMessage");
        Assert.Equal("bob: latest", bobPreview.GetProperty("content").GetString());
        Assert.Equal(bob.Id, bobPreview.GetProperty("senderId").GetGuid());
        Assert.EndsWith("Z", bobPreview.GetProperty("createdAt").GetString());

        var carolPreview = list.Single(c => c.IdOf() == withCarol).GetProperty("lastMessage");
        Assert.Equal("carol: only", carolPreview.GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_long_preview_is_cut_to_one_hundred_characters()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var id = (await client.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.AddMessageAsync(id, bob.Id, new string('x', 250), DateTime.UtcNow);

        var preview = (await client.ListConversationsAsync()).Single().GetProperty("lastMessage");

        Assert.Equal(100, preview.GetProperty("content").GetString()!.Length);
    }

    [Fact]
    public async Task A_deleted_last_message_never_shows_its_text_even_if_the_text_is_still_stored()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var id = (await client.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.AddMessageAsync(id, bob.Id, "secret that was deleted", DateTime.UtcNow, deleted: true);

        var preview = (await client.ListConversationsAsync()).Single().GetProperty("lastMessage");

        // Deletion normally clears the text, but the preview must not depend on that: a row flagged
        // as deleted never reveals its content. The row itself survives, so the preview still exists.
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("content").ValueKind);
        Assert.Equal(bob.Id, preview.GetProperty("senderId").GetGuid());
    }

    // The other person of a direct chat is who the client shows presence for. Like the name, it is
    // resolved per viewer; a group has no single other person.
    [Fact]
    public async Task A_direct_conversation_carries_the_other_persons_id_for_each_viewer_and_a_group_carries_none()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var created = await aliceClient.CreateDirectAsync(bob.Id);
        var direct = created.IdOf();
        var group = (await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        var forAlice = await aliceClient.ListConversationsAsync();
        var forBob = await bobClient.ListConversationsAsync();
        var single = await bobClient.GetFromJsonAsync<JsonElement>($"/api/conversations/{direct}");

        Assert.Equal(bob.Id, created.GetProperty("otherUserId").GetGuid());
        Assert.Equal(bob.Id, forAlice.Single(c => c.IdOf() == direct).GetProperty("otherUserId").GetGuid());
        Assert.Equal(alice.Id, forBob.Single(c => c.IdOf() == direct).GetProperty("otherUserId").GetGuid());
        Assert.Equal(alice.Id, single.GetProperty("otherUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, forAlice.Single(c => c.IdOf() == group).GetProperty("otherUserId").ValueKind);
    }

    // History and the unread count only cover messages sent since a member joined (D-90). The
    // preview is made of a message too, so it must follow the same rule: otherwise a late joiner could
    // read the last thing said before they arrived in the sidebar even though history hides it.
    [Fact]
    public async Task The_preview_never_shows_a_message_from_before_the_member_joined()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        var onlyBefore = (await aliceClient.CreateGroupAsync("Only before", bob.Id, carol.Id)).IdOf();
        var both = (await aliceClient.CreateGroupAsync("Before and after", bob.Id, carol.Id)).IdOf();
        var joined = DateTime.UtcNow.AddMinutes(-30);
        foreach (var group in new[] { onlyBefore, both })
        {
            await _factory.BackdateJoinAsync(group);
            await _factory.ExecuteDbAsync(db => db.ConversationMembers
                .Where(m => m.ConversationId == group && m.UserId == carol.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, joined)));
            await _factory.AddMessageAsync(group, bob.Id, "said before carol joined", joined.AddMinutes(-10));
        }
        await _factory.AddMessageAsync(both, bob.Id, "said after carol joined", joined.AddMinutes(10));

        var forCarol = await carolClient.ListConversationsAsync();
        var forAlice = await aliceClient.ListConversationsAsync();
        var single = await carolClient.GetFromJsonAsync<JsonElement>($"/api/conversations/{onlyBefore}");

        Assert.Equal(JsonValueKind.Null, forCarol.Single(c => c.IdOf() == onlyBefore).GetProperty("lastMessage").ValueKind);
        Assert.Equal("said after carol joined",
            forCarol.Single(c => c.IdOf() == both).GetProperty("lastMessage").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, single.GetProperty("lastMessage").ValueKind);
        // The member who was there from the start still sees the latest message of each.
        Assert.Equal("said before carol joined",
            forAlice.Single(c => c.IdOf() == onlyBefore).GetProperty("lastMessage").GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_conversation_the_user_has_left_stays_in_their_list_marked_as_left_and_is_unchanged_for_the_others()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var id = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        Assert.Single(await bobClient.ListConversationsAsync());

        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == id && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var bobsView = Assert.Single(await bobClient.ListConversationsAsync());
        Assert.Equal(id, bobsView.IdOf());
        Assert.NotEqual(JsonValueKind.Null, bobsView.GetProperty("leftAt").ValueKind);
        var alicesView = Assert.Single(await aliceClient.ListConversationsAsync());
        Assert.Equal(JsonValueKind.Null, alicesView.GetProperty("leftAt").ValueKind);
    }

    [Fact]
    public async Task A_new_user_has_an_empty_list()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        Assert.Empty(await client.ListConversationsAsync());
    }

    // The list runs the same number of SQL queries whether the user has one conversation or
    // eight with messages. If the previews or names were loaded per row this count would grow.
    [Fact]
    public async Task The_number_of_queries_does_not_grow_with_the_number_of_conversations()
    {
        var counter = new QueryCounter();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(counter))));

        var few = await _factory.RegisterUserAsync("few");
        var many = await _factory.RegisterUserAsync("many");
        var fewFriend = await _factory.RegisterUserAsync("friend");

        using (var setup = _factory.CreateClientFor(few))
            await setup.CreateDirectAsync(fewFriend.Id);

        using (var setup = _factory.CreateClientFor(many))
        {
            for (var i = 0; i < 4; i++)
            {
                var friend = await _factory.RegisterUserAsync("friend");
                var direct = (await setup.CreateDirectAsync(friend.Id)).IdOf();
                await _factory.AddMessageAsync(direct, friend.Id, $"hello {i}", DateTime.UtcNow);
                var group = (await setup.CreateGroupAsync($"Group {i}", friend.Id)).IdOf();
                await _factory.AddMessageAsync(group, friend.Id, $"group {i}", DateTime.UtcNow);
            }
        }

        int Measure(TestUser user)
        {
            using var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);

            counter.Reset();
            client.GetAsync("/api/conversations").GetAwaiter().GetResult().EnsureSuccessStatusCode();
            return counter.Count;
        }

        var queriesForOne = Measure(few);
        var queriesForEight = Measure(many);

        Assert.Equal(1, queriesForOne);
        Assert.Equal(queriesForOne, queriesForEight);
    }

    private Task SetUpdatedAtAsync(Guid conversationId, DateTime updatedAt) =>
        _factory.ExecuteDbAsync(db => db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, updatedAt)));
}
