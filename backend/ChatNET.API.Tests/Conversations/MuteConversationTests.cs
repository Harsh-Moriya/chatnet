using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Conversations;

// Muting is a private choice of one member: it quiets the alerts for new messages on that person's own screens and
// changes nothing for anyone else in the conversation. The server stores it and tells the person's other tabs.
[Collection(IntegrationTestCollection.Name)]
public class MuteConversationTests
{
    private readonly ChatApiFactory _factory;

    public MuteConversationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid Group)> GroupAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Quiet", bob.Id)).IdOf();
        return (alice, bob, group);
    }

    private Task<bool> StoredMutedAsync(Guid conversation, Guid user) =>
        _factory.QueryDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == conversation && m.UserId == user)
            .Select(m => m.IsMuted)
            .SingleAsync());

    // ##### What it does #####

    [Fact]
    public async Task A_new_conversation_is_not_muted_for_anyone()
    {
        var (alice, bob, group) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        Assert.False((await aliceClient.ListConversationsAsync()).Single(c => c.IdOf() == group).GetProperty("isMuted").GetBoolean());
        Assert.False((await bobClient.ListConversationsAsync()).Single(c => c.IdOf() == group).GetProperty("isMuted").GetBoolean());
    }

    [Fact]
    public async Task Muting_answers_204_with_no_body_and_stores_the_choice()
    {
        var (_, bob, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(bob);

        var response = await client.SetMutedResponseAsync(group, true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.True(await StoredMutedAsync(group, bob.Id));
    }

    [Fact]
    public async Task The_list_and_the_single_conversation_both_show_the_choice()
    {
        var (_, bob, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(bob);

        await client.SetMutedAsync(group, true);

        var listed = (await client.ListConversationsAsync()).Single(c => c.IdOf() == group);
        var single = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/conversations/{group}");
        Assert.True(listed.GetProperty("isMuted").GetBoolean());
        Assert.True(single.GetProperty("isMuted").GetBoolean());
    }

    [Fact]
    public async Task Unmuting_turns_it_back_off()
    {
        var (_, bob, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(bob);
        await client.SetMutedAsync(group, true);

        await client.SetMutedAsync(group, false);

        Assert.False(await StoredMutedAsync(group, bob.Id));
        Assert.False((await client.ListConversationsAsync()).Single(c => c.IdOf() == group).GetProperty("isMuted").GetBoolean());
    }

    [Fact]
    public async Task Repeating_the_same_choice_is_harmless()
    {
        var (_, bob, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(bob);

        var first = await client.SetMutedResponseAsync(group, true);
        var second = await client.SetMutedResponseAsync(group, true);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.True(await StoredMutedAsync(group, bob.Id));
    }

    // ##### It is one person's choice #####

    [Fact]
    public async Task Muting_leaves_every_other_member_unmuted()
    {
        var (alice, bob, group) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        await bobClient.SetMutedAsync(group, true);

        Assert.False(await StoredMutedAsync(group, alice.Id));
        Assert.False((await aliceClient.ListConversationsAsync()).Single(c => c.IdOf() == group).GetProperty("isMuted").GetBoolean());
    }

    [Fact]
    public async Task Muting_one_conversation_leaves_the_others_of_the_same_person()
    {
        var (alice, bob, group) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var other = (await aliceClient.CreateGroupAsync("Loud", bob.Id)).IdOf();

        await bobClient.SetMutedAsync(group, true);

        Assert.False(await StoredMutedAsync(other, bob.Id));
    }

    // The badge still counts what arrived: muting quiets the alerts, not the record of what is unread.
    [Fact]
    public async Task A_muted_conversation_still_counts_unread_messages()
    {
        var (alice, bob, group) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        await bobClient.SetMutedAsync(group, true);

        await aliceClient.SendMessageAsync(group, "are you there");

        Assert.Equal(1, await bobClient.UnreadInListAsync(group));
    }

    // ##### Who may mute #####

    [Fact]
    public async Task Only_a_member_may_mute_and_an_unknown_conversation_gets_the_same_refusal()
    {
        var (_, _, group) = await GroupAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var intruderClient = _factory.CreateClientFor(intruder);

        var existing = await intruderClient.SetMutedResponseAsync(group, true);
        var unknown = await intruderClient.SetMutedResponseAsync(Guid.NewGuid(), true);

        Assert.Equal(HttpStatusCode.Forbidden, existing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal((await existing.ReadProblemAsync()).Detail, (await unknown.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_refused_attempt_changes_nobodys_choice()
    {
        var (alice, bob, group) = await GroupAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var intruderClient = _factory.CreateClientFor(intruder);

        await intruderClient.SetMutedResponseAsync(group, true);

        Assert.False(await StoredMutedAsync(group, alice.Id));
        Assert.False(await StoredMutedAsync(group, bob.Id));
    }

    [Fact]
    public async Task A_member_who_has_left_may_no_longer_mute()
    {
        var (_, bob, group) = await GroupAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await bobClient.SetMutedResponseAsync(group, true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await StoredMutedAsync(group, bob.Id));
    }

    [Fact]
    public async Task Muting_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.SetMutedResponseAsync(Guid.NewGuid(), true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### Validation #####

    // A body that does not say which way to go must not be read as "unmute".
    [Fact]
    public async Task A_body_without_a_choice_is_refused_and_changes_nothing()
    {
        var (_, bob, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(bob);
        await client.SetMutedAsync(group, true);

        var response = await client.PutAsJsonAsync($"/api/conversations/{group}/mute", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Validation Failed", problem.Title);
        Assert.True(problem.Root.GetProperty("errors").TryGetProperty("IsMuted", out _),
            "The validation error should be reported on the IsMuted field.");
        Assert.True(await StoredMutedAsync(group, bob.Id));
    }

    [Fact]
    public async Task A_body_that_is_not_a_boolean_is_refused()
    {
        var (_, bob, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(bob);

        var response = await client.PutAsJsonAsync($"/api/conversations/{group}/mute", new { isMuted = "yes" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await StoredMutedAsync(group, bob.Id));
    }

    // ##### Who is told #####

    [Fact]
    public async Task All_of_my_open_tabs_are_told_and_nobody_else_is()
    {
        var (alice, bob, group) = await GroupAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        await using var thisTab = await HubClient.ConnectAsync(_factory, bob);
        await using var otherTab = await HubClient.ConnectAsync(_factory, bob);
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);

        await bobClient.SetMutedAsync(group, true);

        foreach (var tab in new[] { thisTab, otherTab })
            Assert.Equal(new MuteEvent(group, true), await tab.WaitForMuteChangedAsync(e => e.ConversationId == group));

        Assert.False(await aliceTab.ReceivedMuteChangedAsync());
    }

    [Fact]
    public async Task Unmuting_is_pushed_too()
    {
        var (_, bob, group) = await GroupAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        await bobClient.SetMutedAsync(group, true);
        await using var tab = await HubClient.ConnectAsync(_factory, bob);

        await bobClient.SetMutedAsync(group, false);

        Assert.Equal(new MuteEvent(group, false), await tab.WaitForMuteChangedAsync(e => e.ConversationId == group));
    }

    [Fact]
    public async Task A_refused_attempt_tells_nobody()
    {
        var (_, bob, group) = await GroupAsync();
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var intruderClient = _factory.CreateClientFor(intruder);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        await using var intruderTab = await HubClient.ConnectAsync(_factory, intruder);

        await intruderClient.SetMutedResponseAsync(group, true);

        Assert.False(await bobTab.ReceivedMuteChangedAsync());
        Assert.False(await intruderTab.ReceivedMuteChangedAsync());
    }

    [Fact]
    public async Task A_tab_on_another_server_instance_is_told_too()
    {
        var (_, bob, group) = await GroupAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await using var tab = await HubClient.ConnectAsync(otherInstance, bob);

        await bobClient.SetMutedAsync(group, true);

        await tab.WaitForMuteChangedAsync(e => e.ConversationId == group);
    }

    // The choice is saved before the push is attempted, so a failing push (Redis down) must not turn a successful
    // request into an error. The other tabs see it on their next load.
    [Fact]
    public async Task A_failing_push_does_not_fail_the_request_and_is_logged_with_ids_only()
    {
        var capture = new CapturingLoggerProvider();
        var (_, bob, group) = await GroupAsync();
        using var brokenHub = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
            services.AddLogging(logging => logging.AddProvider(capture));
        }));
        using var client = brokenHub.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bob.Token);

        var response = await client.SetMutedResponseAsync(group, true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await StoredMutedAsync(group, bob.Id));
        Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains("SetMutedHandler") && e.Contains(group.ToString()));
    }
}
