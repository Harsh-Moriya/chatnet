using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Users;

// A person reads their own profile and changes their display name. The change is told to the people who share a
// conversation with them and to their own tabs, and to nobody else.
[Collection(IntegrationTestCollection.Name)]
public class ProfileTests
{
    private readonly ChatApiFactory _factory;

    public ProfileTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### Reading #####

    [Fact]
    public async Task A_person_reads_their_own_profile_and_has_no_picture_yet()
    {
        var alice = await _factory.RegisterUserAsync("profile");
        using var client = _factory.CreateClientFor(alice);

        var me = await client.GetMeAsync();

        Assert.Equal(alice.Id, me.GetProperty("id").GetGuid());
        Assert.Equal(alice.UserName, me.GetProperty("userName").GetString());
        Assert.Equal(alice.DisplayName, me.GetProperty("displayName").GetString());
        Assert.Null(me.AvatarUrlOf());
    }

    [Fact]
    public async Task Without_a_token_there_is_no_profile()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetMeResponseAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### Changing the name #####

    [Fact]
    public async Task A_new_name_is_trimmed_stored_and_found_by_search()
    {
        var alice = await _factory.RegisterUserAsync("profile");
        var bob = await _factory.RegisterUserAsync("seeker");
        using var client = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var name = $"Renamed {Guid.NewGuid():N}";

        var response = await client.UpdateProfileResponseAsync($"  {name}  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, body.GetProperty("displayName").GetString());
        Assert.Equal(alice.UserName, body.GetProperty("userName").GetString());
        Assert.Equal(name, (await client.GetMeAsync()).GetProperty("displayName").GetString());

        var found = Assert.Single(await bobClient.SearchUsersAsync(name));
        Assert.Equal(alice.Id, found.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_name_that_is_missing_or_blank_is_refused(string? displayName)
    {
        var alice = await _factory.RegisterUserAsync("profile");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.UpdateProfileResponseAsync(displayName);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(alice.DisplayName, (await client.GetMeAsync()).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_name_of_a_hundred_characters_is_kept_and_a_hundred_and_one_is_refused()
    {
        var alice = await _factory.RegisterUserAsync("profile");
        using var client = _factory.CreateClientFor(alice);

        var longest = await client.UpdateProfileResponseAsync(new string('n', 100));
        var tooLong = await client.UpdateProfileResponseAsync(new string('n', 101));

        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(100, (await client.GetMeAsync()).GetProperty("displayName").GetString()!.Length);
    }

    [Fact]
    public async Task A_deactivated_account_cannot_change_its_profile_with_the_token_it_still_holds()
    {
        var alice = await _factory.RegisterUserAsync("profile");
        using var client = _factory.CreateClientFor(alice);
        await _factory.DeactivateAsync(alice);

        var response = await client.UpdateProfileResponseAsync("Someone Else");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ##### Who is told #####

    [Fact]
    public async Task The_people_who_share_a_conversation_and_the_persons_own_tabs_are_told_and_nobody_else()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        await using var strangerTab = await HubClient.ConnectAsync(_factory, stranger);
        using var client = _factory.CreateClientFor(alice);
        var name = $"Told {Guid.NewGuid():N}";

        await client.UpdateProfileResponseAsync(name);

        var toBob = await bobTab.WaitForProfileChangedAsync(alice.Id);
        Assert.Equal(name, toBob.GetProperty("displayName").GetString());
        Assert.Equal(alice.UserName, toBob.GetProperty("userName").GetString());
        Assert.Equal(name, (await aliceTab.WaitForProfileChangedAsync(alice.Id)).GetProperty("displayName").GetString());
        Assert.False(await strangerTab.ReceivedProfileChangedAsync(alice.Id));
    }

    [Fact]
    public async Task People_of_the_same_group_are_told_without_a_direct_chat_between_them()
    {
        var alice = await _factory.RegisterUserAsync("owner");
        var bob = await _factory.RegisterUserAsync("member");
        using var client = _factory.CreateClientFor(alice);
        await client.CreateGroupAsync("Team", bob.Id);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);

        await client.UpdateProfileResponseAsync($"Group mate {Guid.NewGuid():N}");

        await bobTab.WaitForProfileChangedAsync(alice.Id);
    }

    [Fact]
    public async Task Saying_the_same_name_again_changes_nothing_and_tells_nobody()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        using var client = _factory.CreateClientFor(alice);
        var name = $"Once {Guid.NewGuid():N}";
        await client.UpdateProfileResponseAsync(name);
        await bobTab.WaitForProfileChangedAsync(alice.Id);
        var rowsBefore = (await _factory.OutboxRowsAboutAsync("userId", alice.Id)).Count(r => r.Kind == ProfileChanged.Name);

        var again = await client.UpdateProfileResponseAsync(name);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // Waits out the quiet period, so a second push would be here by now.
        Assert.True(await bobTab.ReceivedProfileChangedAsync(alice.Id));
        Assert.Single(bobTab.ProfileChanges, p => p.GetProperty("id").GetGuid() == alice.Id);
        var rowsAfter = (await _factory.OutboxRowsAboutAsync("userId", alice.Id)).Count(r => r.Kind == ProfileChanged.Name);
        Assert.Equal(rowsBefore, rowsAfter);
    }

    [Fact]
    public async Task The_push_is_kept_as_an_outbox_row_that_the_request_marks_done()
    {
        var alice = await _factory.RegisterUserAsync("profile");
        using var client = _factory.CreateClientFor(alice);

        await client.UpdateProfileResponseAsync($"Stored {Guid.NewGuid():N}");

        var row = Assert.Single(
            await _factory.OutboxRowsAboutAsync("userId", alice.Id), r => r.Kind == ProfileChanged.Name);
        Assert.NotNull(row.DispatchedAt);
    }
}
