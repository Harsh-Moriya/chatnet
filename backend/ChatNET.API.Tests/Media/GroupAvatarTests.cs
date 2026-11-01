using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Media;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Media;

// A group's picture: the owner or an admin picks it, everyone in the group sees it, and nobody else may touch it.
[Collection(IntegrationTestCollection.Name)]
public class GroupAvatarTests
{
    private readonly ChatApiFactory _factory;

    public GroupAvatarTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // An owner, an admin, a plain member and a person outside the group.
    private async Task<(TestUser Owner, TestUser Admin, TestUser Member, TestUser Outsider, Guid Group)> GroupAsync()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var admin = await _factory.RegisterUserAsync("admin");
        var member = await _factory.RegisterUserAsync("member");
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(owner);
        var group = (await client.CreateGroupAsync("Crew", admin.Id, member.Id)).IdOf();
        (await client.ChangeRoleResponseAsync(group, admin.Id, "Admin")).EnsureSuccessStatusCode();

        return (owner, admin, member, outsider, group);
    }

    // ##### Who may change it #####

    [Fact]
    public async Task The_owner_sets_the_picture_and_everyone_in_the_group_sees_it()
    {
        var (owner, admin, member, _, group) = await GroupAsync();
        using var ownerClient = _factory.CreateClientFor(owner);
        using var memberClient = _factory.CreateClientFor(member);

        var address = await ownerClient.SetAvatarAsync(group);

        Assert.Matches($"^/api/avatars/groups/{group}/[0-9a-f-]{{36}}$", address);
        Assert.True(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(address)));
        foreach (var person in new[] { owner, admin, member })
        {
            using var client = _factory.CreateClientFor(person);
            var entry = (await client.ListConversationsAsync()).Single(c => c.IdOf() == group);
            Assert.Equal(address, entry.AvatarUrlOf());
        }

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(address)).StatusCode);
    }

    [Fact]
    public async Task An_admin_may_set_and_remove_the_picture()
    {
        var (_, admin, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(admin);

        var address = await client.SetAvatarAsync(group);
        var removal = await client.RemoveAvatarResponseAsync(group);

        Assert.NotNull(address);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);
        Assert.False(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(address)));
    }

    [Fact]
    public async Task A_plain_member_cannot_ask_set_or_remove()
    {
        var (owner, _, member, _, group) = await GroupAsync();
        using var ownerClient = _factory.CreateClientFor(owner);
        using var memberClient = _factory.CreateClientFor(member);
        var address = await ownerClient.SetAvatarAsync(group);
        var memberUpload = AvatarKeys.NewUploadKey(AvatarOwner.Group, group);
        await _factory.PutObjectAsync(memberUpload, ProfileApi.SmallPicture);

        var ticket = await memberClient.AvatarUploadResponseAsync(group, "image/png", 100);
        var set = await memberClient.SetAvatarResponseAsync(group, memberUpload);
        var remove = await memberClient.RemoveAvatarResponseAsync(group);

        Assert.Equal(HttpStatusCode.Forbidden, ticket.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, set.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.True(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(address)));
        var stored = await _factory.ObjectKeysAsync($"avatars/groups/{group}/");
        Assert.Equal(ProfileApi.KeyOf(address), Assert.Single(stored));
    }

    [Fact]
    public async Task Someone_outside_the_group_gets_the_answer_every_endpoint_gives()
    {
        var (_, _, _, outsider, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(outsider);

        var ticket = await client.AvatarUploadResponseAsync(group, "image/png", 100);
        var unknown = await client.AvatarUploadResponseAsync(Guid.NewGuid(), "image/png", 100);
        var remove = await client.RemoveAvatarResponseAsync(group);

        Assert.Equal(HttpStatusCode.Forbidden, ticket.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.Equal(Detail(await ticket.Content.ReadAsStringAsync()), Detail(await unknown.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Someone_who_left_the_group_cannot_change_its_picture()
    {
        var (_, admin, _, _, group) = await GroupAsync();
        using var adminClient = _factory.CreateClientFor(admin);
        (await adminClient.LeaveResponseAsync(group)).EnsureSuccessStatusCode();

        var ticket = await adminClient.AvatarUploadResponseAsync(group, "image/png", 100);

        Assert.Equal(HttpStatusCode.Forbidden, ticket.StatusCode);
    }

    [Fact]
    public async Task A_direct_chat_has_no_picture_to_set()
    {
        var (alice, _, direct) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var ticket = await client.AvatarUploadResponseAsync(direct, "image/png", 100);

        Assert.Equal(HttpStatusCode.Forbidden, ticket.StatusCode);
    }

    // A ticket is refused for a direct chat, so an upload in its folder has to be placed by hand. Even then the picture is
    // refused for what it is, a conversation that is not a group, and the copy that was made is not kept.
    [Fact]
    public async Task Even_an_upload_placed_in_a_direct_chats_folder_cannot_become_its_picture()
    {
        var (alice, _, direct) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = AvatarKeys.NewUploadKey(AvatarOwner.Group, direct);
        await _factory.PutObjectAsync(key, ProfileApi.SmallPicture);

        var response = await client.SetAvatarResponseAsync(direct, key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await _factory.ObjectKeysAsync($"avatars/groups/{direct}/"));
    }

    [Fact]
    public async Task A_picture_is_checked_the_same_way_as_a_persons()
    {
        var (owner, _, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.AvatarUploadResponseAsync(group, "image/svg+xml", 100)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.AvatarUploadResponseAsync(group, "image/png", AvatarRules.MaxBytes + 1)).StatusCode);

        var persons = AvatarKeys.NewUploadKey(AvatarOwner.User, owner.Id);
        await _factory.PutObjectAsync(persons, ProfileApi.SmallPicture);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SetAvatarResponseAsync(group, persons)).StatusCode);
    }

    // ##### Telling people #####

    [Fact]
    public async Task The_people_of_the_group_are_told_when_the_picture_changes_and_when_it_goes()
    {
        var (owner, admin, member, outsider, group) = await GroupAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, member);
        await using var adminTab = await HubClient.ConnectAsync(_factory, admin);
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, outsider);
        using var client = _factory.CreateClientFor(owner);

        var address = await client.SetAvatarAsync(group);

        var set = await memberTab.WaitForConversationUpdatedAsync(group);
        Assert.Equal(address, set.AvatarUrlOf());
        Assert.Equal("Crew", set.GetProperty("name").GetString());
        await adminTab.WaitForConversationUpdatedAsync(group);

        await client.RemoveAvatarResponseAsync(group);

        await WaitUntilAsync(() => memberTab.ConversationUpdates.Count(u => u.GetProperty("id").GetGuid() == group) == 2);
        Assert.Null(memberTab.ConversationUpdates.Last(u => u.GetProperty("id").GetGuid() == group).AvatarUrlOf());
        Assert.False(await outsiderTab.ReceivedConversationUpdatedAsync(group));
    }

    [Fact]
    public async Task Someone_removed_from_the_group_is_not_told_about_its_picture()
    {
        var (owner, _, member, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        (await client.RemoveMemberResponseAsync(group, member.Id)).EnsureSuccessStatusCode();
        await using var memberTab = await HubClient.ConnectAsync(_factory, member);

        await client.SetAvatarAsync(group);

        Assert.False(await memberTab.ReceivedConversationUpdatedAsync(group));
    }

    // ##### Replacing, removing, and sharing the lock #####

    [Fact]
    public async Task A_new_picture_removes_the_old_object()
    {
        var (owner, _, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        var first = await client.SetAvatarAsync(group);

        var second = await client.SetAvatarAsync(group);

        Assert.False(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(first)));
        Assert.True(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(second)));
    }

    [Fact]
    public async Task Removing_a_picture_that_is_not_there_is_a_200_that_says_nothing()
    {
        var (owner, _, member, _, group) = await GroupAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, member);
        using var client = _factory.CreateClientFor(owner);

        var response = await client.RemoveAvatarResponseAsync(group);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<JsonElement>()).AvatarUrlOf());
        Assert.False(await memberTab.ReceivedConversationUpdatedAsync(group));
    }

    [Fact]
    public async Task A_rename_and_a_new_picture_made_at_once_both_stand()
    {
        var (owner, admin, _, _, group) = await GroupAsync();
        using var ownerClient = _factory.CreateClientFor(owner);
        using var adminClient = _factory.CreateClientFor(admin);
        var upload = await ownerClient.UploadAvatarAsync(group);

        var responses = await Task.WhenAll(
            adminClient.RenameResponseAsync(group, "Renamed crew"),
            ownerClient.SetAvatarResponseAsync(group, upload));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var entry = (await ownerClient.ListConversationsAsync()).Single(c => c.IdOf() == group);
        Assert.Equal("Renamed crew", entry.GetProperty("name").GetString());
        Assert.NotNull(entry.AvatarUrlOf());
    }

    // A row that asks for a picture to be removed is retried by the relay, possibly after the picture was set again. It
    // must never take away a picture people can see, and a group's picture counts as much as a person's.
    [Fact]
    public async Task A_removal_that_names_a_groups_picture_in_use_removes_nothing()
    {
        var (owner, _, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        var inUse = ProfileApi.KeyOf(await client.SetAvatarAsync(group));
        var now = DateTime.UtcNow;
        var rowId = Guid.CreateVersion7();
        await _factory.ExecuteDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = rowId,
                Kind = AvatarFileRemoved.Name,
                Payload = OutboxEvents.Serialize(new AvatarFileRemoved(inUse)),
                CreatedAt = now.AddMinutes(-10),
                NextAttemptAt = now.AddMinutes(-10),
            });
            await db.SaveChangesAsync();
        });

        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        clock.Advance(TimeSpan.FromMinutes(3));
        await relayHost.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        Assert.True(await _factory.ObjectExistsAsync(inUse));
        Assert.NotNull((await _factory.OutboxRowAsync(rowId)).DispatchedAt);
    }

    [Fact]
    public async Task A_picture_set_and_removed_leaves_nothing_under_the_groups_folder()
    {
        var (owner, _, _, _, group) = await GroupAsync();
        using var client = _factory.CreateClientFor(owner);
        await client.SetAvatarAsync(group);

        await client.RemoveAvatarResponseAsync(group);

        Assert.Empty(await _factory.ObjectKeysAsync($"avatars/groups/{group}/"));
    }

    private static string Detail(string problem) => JsonDocument.Parse(problem).RootElement.GetProperty("detail").GetString()!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not become true.");

            await Task.Delay(50);
        }
    }
}
