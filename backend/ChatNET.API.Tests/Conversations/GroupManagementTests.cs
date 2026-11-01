using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Conversations;

// The five ways to change a group (add people, remove someone, change a role, leave, rename), through the real API: who may,
// what is stored, what comes back, and what each person's open connections are told. The rules themselves are
// GroupMembersTests; what happens when two requests arrive together is MembershipChangeTests.
[Collection(IntegrationTestCollection.Name)]
public class GroupManagementTests
{
    private readonly ChatApiFactory _factory;

    public GroupManagementTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // A group of an owner, an admin and two plain members, and someone who is not in it.
    private sealed record Team(Guid Id, TestUser Owner, TestUser Admin, TestUser Member, TestUser Other, TestUser Outsider);

    private async Task<Team> TeamAsync()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var admin = await _factory.RegisterUserAsync("admin");
        var member = await _factory.RegisterUserAsync("member");
        var other = await _factory.RegisterUserAsync("other");
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(owner);

        var id = (await client.CreateGroupAsync("Team", admin.Id, member.Id, other.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == id && m.UserId == admin.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, MemberRole.Admin)));

        return new Team(id, owner, admin, member, other, outsider);
    }

    private HttpClient As(TestUser user) => _factory.CreateClientFor(user);

    private Task<List<ConversationMember>> RowsAsync(Guid groupId) =>
        _factory.QueryDbAsync(db => db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == groupId).ToListAsync());

    private async Task<ConversationMember> RowAsync(Guid groupId, TestUser user) =>
        (await RowsAsync(groupId)).Single(m => m.UserId == user.Id);

    private async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string? detail = null)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal((int)status, problem.Status);
        if (detail is not null) Assert.Equal(detail, problem.Detail);
    }

    // ##### Adding #####

    [Fact]
    public async Task The_owner_adds_a_person_and_everyone_then_sees_them_in_the_group()
    {
        var team = await TeamAsync();
        var newcomer = await _factory.RegisterUserAsync("newcomer");
        using var owner = As(team.Owner);

        var response = await owner.AddMembersResponseAsync(team.Id, newcomer.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var people = (await response.ReadMembersAsync()).PeopleOf();
        Assert.Equal(5, people.Count);
        Assert.Contains((newcomer.Id, "Member"), people);
        Assert.Equal(people, (await owner.GetMembersAsync(team.Id)).PeopleOf());
        using var newcomerClient = As(newcomer);
        Assert.Contains(team.Id, (await newcomerClient.ListConversationsAsync()).Select(c => c.IdOf()));
    }

    [Fact]
    public async Task An_admin_adds_a_person_too()
    {
        var team = await TeamAsync();
        using var admin = As(team.Admin);

        var response = await admin.AddMembersResponseAsync(team.Id, team.Outsider.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await RowAsync(team.Id, team.Outsider)).LeftAt);
    }

    [Fact]
    public async Task A_plain_member_cannot_add_anyone()
    {
        var team = await TeamAsync();
        using var member = As(team.Member);

        var response = await member.AddMembersResponseAsync(team.Id, team.Outsider.Id);

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "Only the owner or an admin can add people to this group.");
        Assert.DoesNotContain(await RowsAsync(team.Id), m => m.UserId == team.Outsider.Id);
    }

    [Fact]
    public async Task Someone_outside_the_group_gets_the_refusal_every_endpoint_gives()
    {
        var team = await TeamAsync();
        using var outsider = As(team.Outsider);

        var response = await outsider.AddMembersResponseAsync(team.Id, team.Outsider.Id);

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "You are not a member of this conversation.");
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_is_refused_the_same_way()
    {
        var user = await _factory.RegisterUserAsync("lost");
        using var client = As(user);

        var response = await client.AddMembersResponseAsync(Guid.NewGuid(), user.Id);

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "You are not a member of this conversation.");
    }

    [Fact]
    public async Task Nobody_adds_people_to_a_direct_chat()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var third = await _factory.RegisterUserAsync("third");
        using var client = As(alice);

        var response = await client.AddMembersResponseAsync(conversation, third.Id);

        await AssertRefusedAsync(response, HttpStatusCode.Conflict, "Only a group has members to manage.");
    }

    [Fact]
    public async Task A_person_who_does_not_exist_makes_the_whole_request_not_found_and_adds_nobody()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var response = await owner.AddMembersResponseAsync(team.Id, team.Outsider.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(await RowsAsync(team.Id), m => m.UserId == team.Outsider.Id);
    }

    [Fact]
    public async Task A_deactivated_account_cannot_be_added()
    {
        var team = await TeamAsync();
        await _factory.DeactivateAsync(team.Outsider);
        using var owner = As(team.Owner);

        var response = await owner.AddMembersResponseAsync(team.Id, team.Outsider.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Someone_who_may_not_add_learns_nothing_about_which_accounts_exist()
    {
        var team = await TeamAsync();
        using var member = As(team.Member);

        var response = await member.AddMembersResponseAsync(team.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task People_who_are_already_in_are_skipped_and_naming_only_them_is_a_conflict()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var mixed = await owner.AddMembersAsync(team.Id, team.Member.Id, team.Outsider.Id);
        var onlyMembers = await owner.AddMembersResponseAsync(team.Id, team.Member.Id, team.Outsider.Id);

        Assert.Equal(5, mixed.Count);
        await AssertRefusedAsync(onlyMembers, HttpStatusCode.Conflict, "Everyone you named is already in the group.");
    }

    [Fact]
    public async Task An_empty_list_and_a_list_over_the_limit_are_refused()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var none = await owner.AddMembersResponseAsync(team.Id);
        var tooMany = await owner.AddMembersResponseAsync(team.Id, Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.NotNull((await none.ReadProblemAsync()).Errors);
    }

    [Fact]
    public async Task A_person_who_left_comes_back_with_a_new_join_time_and_sees_nothing_older()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        using var member = As(team.Member);
        await owner.SendMessageAsync(team.Id, "from before they left");
        await member.LeaveResponseAsync(team.Id);
        await owner.SendMessageAsync(team.Id, "while they were away");
        var before = (await RowAsync(team.Id, team.Member)).JoinedAt;

        await owner.AddMembersAsync(team.Id, team.Member.Id);
        await owner.SendMessageAsync(team.Id, "after they came back");

        var row = await RowAsync(team.Id, team.Member);
        Assert.Null(row.LeftAt);
        Assert.True(row.JoinedAt > before);
        Assert.Equal(MemberRole.Member, row.Role);
        Assert.Equal(["after they came back"], (await member.GetHistoryAsync(team.Id)).Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task The_person_added_is_told_and_so_is_everyone_in_the_group_but_nobody_else()
    {
        var team = await TeamAsync();
        var newcomer = await _factory.RegisterUserAsync("newcomer");
        await using var ownerTab = await HubClient.ConnectAsync(_factory, team.Owner);
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var newcomerTab = await HubClient.ConnectAsync(_factory, newcomer);
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, team.Outsider);
        using var owner = As(team.Owner);

        await owner.AddMembersAsync(team.Id, newcomer.Id);

        var added = await newcomerTab.WaitForAddedAsync(team.Id);
        Assert.Equal("Team", added.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, added.GetProperty("leftAt").ValueKind);
        foreach (var tab in new[] { ownerTab, memberTab, newcomerTab })
        {
            var change = await tab.WaitForMembersChangedAsync(team.Id);
            Assert.Equal([newcomer.Id], change.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()));
            Assert.Empty(change.GetProperty("removedUserIds").EnumerateArray());
        }

        Assert.False(await outsiderTab.ReceivedMembersChangedAsync(team.Id));
        Assert.False(await outsiderTab.ReceivedAddedAsync(team.Id));
        using var newcomerClient = As(newcomer);
        var bell = (await newcomerClient.GetNotificationsAsync()).ItemsOf();
        Assert.Contains(bell, n => n.GetProperty("type").GetString() == "MemberAdded"
                                   && n.GetProperty("conversationId").GetGuid() == team.Id);
    }

    [Fact]
    public async Task Every_push_of_an_add_is_made_by_the_request_and_marked_done_at_once()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        await owner.AddMembersAsync(team.Id, team.Outsider.Id);

        var rows = (await _factory.OutboxRowsAboutAsync("conversationId", team.Id))
            .Where(r => r.Kind is "MembersChanged" or "MembersJoined").ToList();
        Assert.Equal(["MembersChanged", "MembersJoined"], rows.Select(r => r.Kind).Order());
        Assert.All(rows, r => Assert.NotNull(r.DispatchedAt));
        Assert.All(rows, r => Assert.Equal(0, r.Attempts));
    }

    // ##### Removing #####

    [Fact]
    public async Task The_owner_removes_a_member_who_is_then_out_of_the_group()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        using var member = As(team.Member);

        var response = await owner.RemoveMemberResponseAsync(team.Id, team.Member.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(team.Member.Id, (await response.ReadMembersAsync()).PeopleOf().Select(p => p.UserId));
        Assert.NotNull((await RowAsync(team.Id, team.Member)).LeftAt);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetMembersResponseAsync(team.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostMessageAsync(team.Id, "still here?")).StatusCode);
    }

    [Fact]
    public async Task The_owner_removes_an_admin_and_an_admin_removes_a_plain_member()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        using var admin = As(team.Admin);

        Assert.Equal(HttpStatusCode.OK, (await admin.RemoveMemberResponseAsync(team.Id, team.Member.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.RemoveMemberResponseAsync(team.Id, team.Admin.Id)).StatusCode);

        Assert.NotNull((await RowAsync(team.Id, team.Member)).LeftAt);
        Assert.NotNull((await RowAsync(team.Id, team.Admin)).LeftAt);
    }

    [Fact]
    public async Task An_admin_cannot_remove_the_owner_or_another_admin()
    {
        var team = await TeamAsync();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == team.Id && m.UserId == team.Other.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, MemberRole.Admin)));
        using var admin = As(team.Admin);

        var owner = await admin.RemoveMemberResponseAsync(team.Id, team.Owner.Id);
        var other = await admin.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        await AssertRefusedAsync(owner, HttpStatusCode.Forbidden, "An admin can remove members, not other admins or the owner.");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.All(await RowsAsync(team.Id), m => Assert.Null(m.LeftAt));
    }

    [Fact]
    public async Task A_plain_member_cannot_remove_anyone()
    {
        var team = await TeamAsync();
        using var member = As(team.Member);

        var response = await member.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "Only the owner or an admin can remove people from this group.");
    }

    [Fact]
    public async Task Removing_oneself_is_a_conflict_and_removing_someone_who_is_not_in_the_group_is_not_found()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var self = await owner.RemoveMemberResponseAsync(team.Id, team.Owner.Id);
        var stranger = await owner.RemoveMemberResponseAsync(team.Id, team.Outsider.Id);
        await owner.RemoveMemberResponseAsync(team.Id, team.Member.Id);
        var again = await owner.RemoveMemberResponseAsync(team.Id, team.Member.Id);

        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Nobody_is_removed_from_a_direct_chat_and_an_outsider_cannot_remove_anyone()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var team = await TeamAsync();
        using var aliceClient = As(alice);
        using var outsider = As(team.Outsider);

        var direct = await aliceClient.RemoveMemberResponseAsync(conversation, bob.Id);
        var stranger = await outsider.RemoveMemberResponseAsync(team.Id, team.Member.Id);

        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        await AssertRefusedAsync(stranger, HttpStatusCode.Forbidden, "You are not a member of this conversation.");
    }

    [Fact]
    public async Task The_person_removed_is_told_in_every_tab_and_the_others_hear_who_is_gone_but_not_the_one_removed()
    {
        var team = await TeamAsync();
        await using var removedTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var removedSecondTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var ownerTab = await HubClient.ConnectAsync(_factory, team.Owner);
        await using var otherTab = await HubClient.ConnectAsync(_factory, team.Other);
        using var owner = As(team.Owner);

        await owner.RemoveMemberResponseAsync(team.Id, team.Member.Id);

        var leftAt = (await RowAsync(team.Id, team.Member)).LeftAt!.Value;
        foreach (var tab in new[] { removedTab, removedSecondTab })
        {
            var removed = await tab.WaitForRemovedAsync(team.Id);
            Assert.Equal(leftAt, removed.LeftAt);
            Assert.False(await tab.ReceivedMembersChangedAsync(team.Id));
        }

        foreach (var tab in new[] { ownerTab, otherTab })
        {
            var change = await tab.WaitForMembersChangedAsync(team.Id);
            Assert.Equal([team.Member.Id], change.GetProperty("removedUserIds").EnumerateArray().Select(id => id.GetGuid()));
            Assert.Empty(change.GetProperty("members").EnumerateArray());
            Assert.False(await tab.ReceivedRemovedAsync(team.Id));
        }
    }

    // ##### Roles #####

    [Fact]
    public async Task The_owner_makes_a_member_an_admin_and_then_a_member_again()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var promoted = await owner.ChangeRoleResponseAsync(team.Id, team.Member.Id, "Admin");
        var roleAfterPromotion = (await RowAsync(team.Id, team.Member)).Role;
        var demoted = await owner.ChangeRoleResponseAsync(team.Id, team.Member.Id, "Member");

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Contains((team.Member.Id, "Admin"), (await promoted.ReadMembersAsync()).PeopleOf());
        Assert.Equal(MemberRole.Admin, roleAfterPromotion);
        Assert.Equal(MemberRole.Member, (await RowAsync(team.Id, team.Member)).Role);
        Assert.Contains((team.Member.Id, "Member"), (await demoted.ReadMembersAsync()).PeopleOf());
    }

    [Fact]
    public async Task Making_someone_the_owner_makes_the_old_owner_an_admin_and_leaves_one_owner()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var response = await owner.ChangeRoleResponseAsync(team.Id, team.Member.Id, "Owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await RowsAsync(team.Id);
        Assert.Equal([team.Member.Id], rows.Where(m => m.Role == MemberRole.Owner).Select(m => m.UserId));
        Assert.Equal(MemberRole.Admin, rows.Single(m => m.UserId == team.Owner.Id).Role);
    }

    [Fact]
    public async Task An_admin_cannot_change_roles_and_nobody_changes_their_own()
    {
        var team = await TeamAsync();
        using var admin = As(team.Admin);
        using var owner = As(team.Owner);

        var byAdmin = await admin.ChangeRoleResponseAsync(team.Id, team.Member.Id, "Admin");
        var own = await owner.ChangeRoleResponseAsync(team.Id, team.Owner.Id, "Member");

        await AssertRefusedAsync(byAdmin, HttpStatusCode.Forbidden, "Only the owner can change roles.");
        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);
        Assert.Equal(MemberRole.Member, (await RowAsync(team.Id, team.Member)).Role);
    }

    [Fact]
    public async Task A_role_that_is_missing_or_unknown_is_refused_and_a_person_who_is_not_in_the_group_is_not_found()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var missing = await owner.ChangeRoleResponseAsync(team.Id, team.Member.Id, null);
        var unknown = await owner.ChangeRoleResponseAsync(team.Id, team.Member.Id, "Boss");
        var stranger = await owner.ChangeRoleResponseAsync(team.Id, team.Outsider.Id, "Admin");

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    [Fact]
    public async Task Asking_for_the_role_someone_has_changes_nothing_and_says_nothing()
    {
        var team = await TeamAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        using var owner = As(team.Owner);

        var response = await owner.ChangeRoleResponseAsync(team.Id, team.Admin.Id, "Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await memberTab.ReceivedMembersChangedAsync(team.Id));
        Assert.DoesNotContain(await _factory.OutboxRowsAboutAsync("conversationId", team.Id), r => r.Kind == "MembersChanged");
    }

    [Fact]
    public async Task A_change_of_role_is_pushed_to_the_people_in_the_group_with_the_entry_as_it_is_now()
    {
        var team = await TeamAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, team.Outsider);
        using var owner = As(team.Owner);

        await owner.ChangeRoleResponseAsync(team.Id, team.Other.Id, "Admin");

        var change = await memberTab.WaitForMembersChangedAsync(team.Id);
        var entry = Assert.Single(change.GetProperty("members").EnumerateArray());
        Assert.Equal(team.Other.Id, entry.GetProperty("userId").GetGuid());
        Assert.Equal("Admin", entry.GetProperty("role").GetString());
        Assert.False(await outsiderTab.ReceivedMembersChangedAsync(team.Id));
    }

    // ##### Leaving #####

    [Fact]
    public async Task A_member_leaves_and_is_told_when_in_every_tab_and_the_others_hear_it()
    {
        var team = await TeamAsync();
        await using var leaverTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var leaverSecondTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var ownerTab = await HubClient.ConnectAsync(_factory, team.Owner);
        using var member = As(team.Member);

        var response = await member.LeaveResponseAsync(team.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await RowAsync(team.Id, team.Member)).LeftAt!.Value;
        Assert.Equal(stored, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leftAt").GetDateTime());
        foreach (var tab in new[] { leaverTab, leaverSecondTab })
            Assert.Equal(stored, (await tab.WaitForRemovedAsync(team.Id)).LeftAt);
        var change = await ownerTab.WaitForMembersChangedAsync(team.Id);
        Assert.Equal([team.Member.Id], change.GetProperty("removedUserIds").EnumerateArray().Select(id => id.GetGuid()));
    }

    [Fact]
    public async Task Someone_who_left_cannot_read_the_people_of_the_group_or_send_to_it()
    {
        var team = await TeamAsync();
        using var member = As(team.Member);
        await member.LeaveResponseAsync(team.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetMembersResponseAsync(team.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostMessageAsync(team.Id, "hello?")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.LeaveResponseAsync(team.Id)).StatusCode);
    }

    [Fact]
    public async Task Nobody_leaves_a_direct_chat_and_an_outsider_cannot_leave_a_group_they_are_not_in()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var team = await TeamAsync();
        using var aliceClient = As(alice);
        using var outsider = As(team.Outsider);

        Assert.Equal(HttpStatusCode.Conflict, (await aliceClient.LeaveResponseAsync(conversation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.LeaveResponseAsync(team.Id)).StatusCode);
    }

    [Fact]
    public async Task When_the_owner_leaves_the_group_goes_to_the_admin_who_has_been_in_longest_and_everyone_is_told()
    {
        var team = await TeamAsync();
        await using var adminTab = await HubClient.ConnectAsync(_factory, team.Admin);
        using var owner = As(team.Owner);

        var response = await owner.LeaveResponseAsync(team.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await RowsAsync(team.Id);
        Assert.Equal([team.Admin.Id], rows.Where(m => m.LeftAt is null && m.Role == MemberRole.Owner).Select(m => m.UserId));
        var change = await adminTab.WaitForMembersChangedAsync(team.Id);
        var successor = Assert.Single(change.GetProperty("members").EnumerateArray());
        Assert.Equal(team.Admin.Id, successor.GetProperty("userId").GetGuid());
        Assert.Equal("Owner", successor.GetProperty("role").GetString());
        Assert.Equal([team.Owner.Id], change.GetProperty("removedUserIds").EnumerateArray().Select(id => id.GetGuid()));
    }

    [Fact]
    public async Task The_last_person_to_leave_leaves_the_group_with_nobody_in_it_but_not_deleted()
    {
        var team = await TeamAsync();
        foreach (var user in new[] { team.Member, team.Other, team.Admin, team.Owner })
        {
            using var client = As(user);
            Assert.Equal(HttpStatusCode.OK, (await client.LeaveResponseAsync(team.Id)).StatusCode);
        }

        var rows = await RowsAsync(team.Id);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, m => Assert.NotNull(m.LeftAt));
    }

    // ##### Renaming #####

    [Fact]
    public async Task The_owner_and_an_admin_rename_the_group_and_everyone_sees_the_new_name_in_their_list()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        using var admin = As(team.Admin);
        using var member = As(team.Member);
        var before = (await member.ListConversationsAsync()).Single(c => c.IdOf() == team.Id).GetProperty("updatedAt").GetDateTime();

        var byOwner = await owner.RenameResponseAsync(team.Id, "  Crew  ");
        var byAdmin = await admin.RenameResponseAsync(team.Id, "Squad");

        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        var answer = await byOwner.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Crew", answer.GetProperty("name").GetString());
        Assert.Equal(team.Id, answer.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, byAdmin.StatusCode);
        var seen = (await member.ListConversationsAsync()).Single(c => c.IdOf() == team.Id);
        Assert.Equal("Squad", seen.GetProperty("name").GetString());
        Assert.Equal(before, seen.GetProperty("updatedAt").GetDateTime());
    }

    [Fact]
    public async Task A_plain_member_cannot_rename_and_a_name_that_is_blank_or_too_long_is_refused()
    {
        var team = await TeamAsync();
        using var member = As(team.Member);
        using var owner = As(team.Owner);

        var byMember = await member.RenameResponseAsync(team.Id, "Mine");
        var blank = await owner.RenameResponseAsync(team.Id, "   ");
        var missing = await owner.RenameResponseAsync(team.Id, null);
        var tooLong = await owner.RenameResponseAsync(team.Id, new string('x', 101));

        await AssertRefusedAsync(byMember, HttpStatusCode.Forbidden, "Only the owner or an admin can rename this group.");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using var check = As(team.Owner);
        Assert.Equal("Team", (await check.ListConversationsAsync()).Single(c => c.IdOf() == team.Id).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_direct_chat_cannot_be_renamed_and_an_outsider_cannot_rename_a_group()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var team = await TeamAsync();
        using var aliceClient = As(alice);
        using var outsider = As(team.Outsider);

        Assert.Equal(HttpStatusCode.Conflict, (await aliceClient.RenameResponseAsync(conversation, "Us")).StatusCode);
        await AssertRefusedAsync(
            await outsider.RenameResponseAsync(team.Id, "Mine"), HttpStatusCode.Forbidden, "You are not a member of this conversation.");
    }

    [Fact]
    public async Task A_rename_reaches_a_new_member_whose_connection_has_not_joined_the_group_yet()
    {
        var team = await TeamAsync();
        var newcomer = await _factory.RegisterUserAsync("newcomer");
        // Connected before being added, so it is not among the connections of the group until it asks to join: the push
        // has to go to the person, not to the group of connections.
        await using var newcomerTab = await HubClient.ConnectAsync(_factory, newcomer);
        using var owner = As(team.Owner);
        await owner.AddMembersAsync(team.Id, newcomer.Id);

        await owner.RenameResponseAsync(team.Id, "Crew");

        Assert.Equal("Crew", (await newcomerTab.WaitForConversationUpdatedAsync(team.Id)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_rename_is_pushed_to_the_people_in_the_group_and_the_same_name_again_says_nothing()
    {
        var team = await TeamAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, team.Outsider);
        using var owner = As(team.Owner);

        await owner.RenameResponseAsync(team.Id, "Crew");
        await owner.RenameResponseAsync(team.Id, "Crew");

        var update = await memberTab.WaitForConversationUpdatedAsync(team.Id);
        Assert.Equal("Crew", update.GetProperty("name").GetString());
        Assert.Single(memberTab.ConversationUpdates);
        Assert.False(await outsiderTab.ReceivedConversationUpdatedAsync(team.Id));
        Assert.Single(await _factory.OutboxRowsAboutAsync("conversationId", team.Id), r => r.Kind == "ConversationRenamed");
    }
}
