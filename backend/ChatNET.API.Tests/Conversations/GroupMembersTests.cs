using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Membership;
using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Tests.Conversations;

// The rules of who may do what to a group's membership, without a database: the rows are built by hand, the rules change
// them, and the tests read them back. What the rules do under concurrent requests is MembershipChangeTests.
public class GroupMembersTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Start.AddDays(10);

    private readonly Guid _conversationId = Guid.NewGuid();
    private readonly List<ConversationMember> _rows = [];

    // Ids that sort in the order of their number, so a tie in the join time is broken the way a test can predict.
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static readonly Guid Owner = Id(1);
    private static readonly Guid Admin = Id(2);
    private static readonly Guid Member = Id(3);
    private static readonly Guid OtherMember = Id(4);
    private static readonly Guid Outsider = Id(99);

    // An owner, an admin and two members, who joined in that order a day apart.
    public GroupMembersTests()
    {
        Row(Owner, MemberRole.Owner, 0);
        Row(Admin, MemberRole.Admin, 1);
        Row(Member, MemberRole.Member, 2);
        Row(OtherMember, MemberRole.Member, 3);
    }

    private ConversationMember Row(Guid userId, MemberRole role, int joinedDaysAfterStart, bool left = false)
    {
        var row = new ConversationMember
        {
            ConversationId = _conversationId,
            UserId = userId,
            Role = role,
            JoinedAt = Start.AddDays(joinedDaysAfterStart),
            LeftAt = left ? Start.AddDays(joinedDaysAfterStart + 1) : null,
            LastReadAt = Start.AddDays(joinedDaysAfterStart + 1),
            IsMuted = true,
        };
        _rows.Add(row);
        return row;
    }

    private GroupMembers Group() => new(_conversationId, _rows);

    private ConversationMember Of(Guid userId) => _rows.Single(m => m.UserId == userId);

    private List<Guid> ActiveOwners() => _rows.Where(m => m.LeftAt is null && m.Role == MemberRole.Owner).Select(m => m.UserId).ToList();

    // ##### Adding #####

    [Fact]
    public void The_owner_adds_a_new_person_as_a_plain_member_who_joins_now()
    {
        var outcome = Group().Add(Owner, [Outsider], Now);

        var added = Of(Outsider);
        Assert.Equal(MemberRole.Member, added.Role);
        Assert.Equal(Now, added.JoinedAt);
        Assert.Null(added.LeftAt);
        Assert.Equal([Outsider], outcome.Joined);
        Assert.Empty(outcome.Changed);
        Assert.Empty(outcome.Left);
    }

    [Fact]
    public void An_admin_adds_people_too()
    {
        Group().Add(Admin, [Outsider], Now);

        Assert.Null(Of(Outsider).LeftAt);
    }

    [Fact]
    public void A_plain_member_cannot_add_anyone_and_nothing_changes()
    {
        var group = Group();

        Assert.Throws<ForbiddenException>(() => group.Add(Member, [Outsider], Now));

        Assert.DoesNotContain(_rows, m => m.UserId == Outsider);
    }

    [Fact]
    public void Someone_who_is_not_in_the_group_gets_the_same_refusal_as_for_any_conversation()
    {
        var error = Assert.Throws<ForbiddenException>(() => Group().Add(Outsider, [Id(98)], Now));

        Assert.Equal(ConversationErrors.NotAMember, error.Message);
    }

    [Fact]
    public void Someone_who_left_cannot_add_anyone()
    {
        Row(Id(50), MemberRole.Admin, 4, left: true);

        Assert.Throws<ForbiddenException>(() => Group().Add(Id(50), [Outsider], Now));
    }

    [Fact]
    public void A_person_who_left_comes_back_as_a_plain_member_with_a_new_join_time_and_no_read_position()
    {
        var returning = Row(Id(10), MemberRole.Admin, 4, left: true);

        var outcome = Group().Add(Owner, [Id(10)], Now);

        Assert.Null(returning.LeftAt);
        Assert.Equal(Now, returning.JoinedAt);
        Assert.Equal(MemberRole.Member, returning.Role);
        Assert.Null(returning.LastReadAt);
        Assert.Equal([Id(10)], outcome.Joined);
        Assert.Single(_rows, m => m.UserId == Id(10));
    }

    [Fact]
    public void A_person_who_comes_back_keeps_their_choice_to_mute_the_group()
    {
        var returning = Row(Id(10), MemberRole.Member, 4, left: true);

        Group().Add(Owner, [Id(10)], Now);

        Assert.True(returning.IsMuted);
    }

    [Fact]
    public void People_who_are_already_in_the_group_are_left_out_and_the_rest_are_added()
    {
        var outcome = Group().Add(Owner, [Member, Outsider], Now);

        Assert.Equal([Outsider], outcome.Joined);
        Assert.Equal(Start.AddDays(2), Of(Member).JoinedAt);
    }

    [Fact]
    public void Naming_only_people_who_are_already_in_is_a_conflict()
    {
        var group = Group();

        Assert.Throws<ConflictException>(() => group.Add(Owner, [Member, OtherMember], Now));
    }

    [Fact]
    public void The_person_adding_is_not_added_again()
    {
        var outcome = Group().Add(Owner, [Owner, Outsider], Now);

        Assert.Equal([Outsider], outcome.Joined);
    }

    [Fact]
    public void A_person_named_twice_is_added_once()
    {
        var outcome = Group().Add(Owner, [Outsider, Outsider], Now);

        Assert.Equal([Outsider], outcome.Joined);
        Assert.Single(_rows, m => m.UserId == Outsider);
    }

    [Fact]
    public void The_group_may_grow_to_its_limit_and_no_further()
    {
        var room = GroupLimits.MaxActiveMembers - 4;
        var fill = Enumerable.Range(1000, room).Select(Id).ToList();

        Group().Add(Owner, fill, Now);
        Assert.Equal(GroupLimits.MaxActiveMembers, Group().ActiveCount);

        Assert.Throws<ConflictException>(() => Group().Add(Owner, [Outsider], Now));
        Assert.DoesNotContain(_rows, m => m.UserId == Outsider);
    }

    [Fact]
    public void An_add_that_would_pass_the_limit_adds_nobody()
    {
        var room = GroupLimits.MaxActiveMembers - 4;
        var tooMany = Enumerable.Range(1000, room + 1).Select(Id).ToList();

        Assert.Throws<ConflictException>(() => Group().Add(Owner, tooMany, Now));

        Assert.Equal(4, _rows.Count);
    }

    [Fact]
    public void People_who_left_do_not_count_toward_the_limit()
    {
        for (var i = 0; i < 10; i++) Row(Id(2000 + i), MemberRole.Member, 4, left: true);
        var room = GroupLimits.MaxActiveMembers - 4;

        Group().Add(Owner, Enumerable.Range(1000, room).Select(Id).ToList(), Now);

        Assert.Equal(GroupLimits.MaxActiveMembers, Group().ActiveCount);
    }

    // ##### Removing #####

    [Fact]
    public void The_owner_removes_an_admin_and_a_member()
    {
        var group = Group();

        group.Remove(Owner, Admin, Now);
        var outcome = group.Remove(Owner, Member, Now);

        Assert.Equal(Now, Of(Admin).LeftAt);
        Assert.Equal(Now, Of(Member).LeftAt);
        Assert.Equal([Member], outcome.Left);
    }

    [Fact]
    public void An_admin_removes_a_plain_member()
    {
        Group().Remove(Admin, Member, Now);

        Assert.Equal(Now, Of(Member).LeftAt);
    }

    [Fact]
    public void An_admin_cannot_remove_another_admin_or_the_owner()
    {
        Row(Id(5), MemberRole.Admin, 4);
        var group = Group();

        Assert.Throws<ForbiddenException>(() => group.Remove(Admin, Id(5), Now));
        Assert.Throws<ForbiddenException>(() => group.Remove(Admin, Owner, Now));

        Assert.Null(Of(Id(5)).LeftAt);
        Assert.Null(Of(Owner).LeftAt);
    }

    [Fact]
    public void A_plain_member_cannot_remove_anyone()
    {
        Assert.Throws<ForbiddenException>(() => Group().Remove(Member, OtherMember, Now));

        Assert.Null(Of(OtherMember).LeftAt);
    }

    [Fact]
    public void Removing_oneself_is_a_conflict_that_points_to_leaving()
    {
        var error = Assert.Throws<ConflictException>(() => Group().Remove(Owner, Owner, Now));

        Assert.Contains("leave", error.Message);
        Assert.Null(Of(Owner).LeftAt);
    }

    [Fact]
    public void Removing_someone_who_is_not_in_the_group_is_not_found()
    {
        Row(Id(10), MemberRole.Member, 4, left: true);
        var group = Group();

        Assert.Throws<NotFoundException>(() => group.Remove(Owner, Outsider, Now));
        Assert.Throws<NotFoundException>(() => group.Remove(Owner, Id(10), Now));
    }

    [Fact]
    public void Someone_who_left_cannot_remove_anyone()
    {
        Row(Id(50), MemberRole.Admin, 4, left: true);

        Assert.Throws<ForbiddenException>(() => Group().Remove(Id(50), Member, Now));
    }

    // ##### Leaving #####

    [Fact]
    public void A_member_leaves_and_the_group_is_otherwise_untouched()
    {
        var outcome = Group().Leave(Member, Now);

        Assert.Equal(Now, Of(Member).LeftAt);
        Assert.Equal([Member], outcome.Left);
        Assert.Empty(outcome.Changed);
        Assert.Equal([Owner], ActiveOwners());
    }

    [Fact]
    public void Someone_who_is_not_in_the_group_cannot_leave_it()
    {
        Row(Id(10), MemberRole.Member, 4, left: true);
        var group = Group();

        Assert.Throws<ForbiddenException>(() => group.Leave(Outsider, Now));
        Assert.Throws<ForbiddenException>(() => group.Leave(Id(10), Now));
    }

    [Fact]
    public void Leaving_twice_changes_nothing_the_second_time()
    {
        var group = Group();
        group.Leave(Member, Now);

        Assert.Throws<ForbiddenException>(() => group.Leave(Member, Now.AddHours(1)));

        Assert.Equal(Now, Of(Member).LeftAt);
    }

    [Fact]
    public void When_the_owner_leaves_the_admin_who_has_been_in_longest_becomes_the_owner()
    {
        Row(Id(5), MemberRole.Admin, 4);

        var outcome = Group().Leave(Owner, Now);

        Assert.Equal(MemberRole.Owner, Of(Admin).Role);
        Assert.Equal(MemberRole.Admin, Of(Id(5)).Role);
        Assert.Equal([Admin], outcome.Changed);
        Assert.Equal([Owner], outcome.Left);
        Assert.Equal([Admin], ActiveOwners());
    }

    [Fact]
    public void An_admin_comes_before_a_member_who_joined_earlier()
    {
        Of(Admin).Role = MemberRole.Member;
        Row(Id(5), MemberRole.Admin, 9);

        Group().Leave(Owner, Now);

        Assert.Equal(MemberRole.Owner, Of(Id(5)).Role);
    }

    [Fact]
    public void Without_an_admin_the_member_who_has_been_in_longest_becomes_the_owner()
    {
        Of(Admin).Role = MemberRole.Member;

        var outcome = Group().Leave(Owner, Now);

        Assert.Equal(MemberRole.Owner, Of(Admin).Role);
        Assert.Equal([Admin], outcome.Changed);
    }

    [Fact]
    public void The_member_who_joined_first_wins_whatever_the_ids_are()
    {
        // The earliest joiner has the highest id, and the lowest id joined last.
        Of(Admin).Role = MemberRole.Member;
        Of(Admin).JoinedAt = Start.AddDays(5);
        Of(OtherMember).JoinedAt = Start.AddDays(1);

        Group().Leave(Owner, Now);

        Assert.Equal(MemberRole.Owner, Of(OtherMember).Role);
        Assert.Equal(MemberRole.Member, Of(Admin).Role);
    }

    [Fact]
    public void A_tie_in_the_join_time_is_settled_by_the_id_whatever_order_the_rows_come_in()
    {
        Of(Admin).Role = MemberRole.Member;
        Of(Admin).JoinedAt = Of(Member).JoinedAt;
        Of(OtherMember).JoinedAt = Of(Member).JoinedAt;
        _rows.Reverse();

        Group().Leave(Owner, Now);

        Assert.Equal(MemberRole.Owner, Of(Admin).Role);
    }

    [Fact]
    public void People_who_left_are_never_chosen_to_become_the_owner()
    {
        // Two admins, both better placed than the members (a role that comes first, and an earlier join), but both gone.
        var earlierAdmin = Row(Id(5), MemberRole.Admin, 0, left: true);
        Of(Admin).LeftAt = Now.AddDays(-1);

        Group().Leave(Owner, Now);

        Assert.Equal(MemberRole.Owner, Of(Member).Role);
        Assert.Equal(MemberRole.Admin, earlierAdmin.Role);
        Assert.Equal(MemberRole.Admin, Of(Admin).Role);
        Assert.Equal([Member], ActiveOwners());
    }

    [Fact]
    public void The_last_person_to_leave_leaves_a_group_nobody_owns()
    {
        var group = Group();
        group.Leave(Member, Now);
        group.Leave(OtherMember, Now);
        group.Leave(Admin, Now);

        var outcome = group.Leave(Owner, Now);

        Assert.Equal([Owner], outcome.Left);
        Assert.Empty(outcome.Changed);
        Assert.Equal(0, group.ActiveCount);
        Assert.Empty(ActiveOwners());
    }

    // ##### Roles #####

    [Fact]
    public void The_owner_makes_a_member_an_admin_and_an_admin_a_member()
    {
        var group = Group();

        var promoted = group.ChangeRole(Owner, Member, MemberRole.Admin);
        var demoted = group.ChangeRole(Owner, Admin, MemberRole.Member);

        Assert.Equal(MemberRole.Admin, Of(Member).Role);
        Assert.Equal(MemberRole.Member, Of(Admin).Role);
        Assert.Equal([Member], promoted.Changed);
        Assert.Equal([Admin], demoted.Changed);
    }

    [Fact]
    public void Making_someone_the_owner_makes_the_old_owner_an_admin_and_leaves_one_owner()
    {
        var outcome = Group().ChangeRole(Owner, Member, MemberRole.Owner);

        Assert.Equal(MemberRole.Owner, Of(Member).Role);
        Assert.Equal(MemberRole.Admin, Of(Owner).Role);
        Assert.Equal([Member, Owner], outcome.Changed);
        Assert.Equal([Member], ActiveOwners());
    }

    [Fact]
    public void An_admin_cannot_change_roles_and_nothing_changes()
    {
        var group = Group();

        Assert.Throws<ForbiddenException>(() => group.ChangeRole(Admin, Member, MemberRole.Admin));
        Assert.Throws<ForbiddenException>(() => group.ChangeRole(Admin, Owner, MemberRole.Member));

        Assert.Equal(MemberRole.Member, Of(Member).Role);
        Assert.Equal(MemberRole.Owner, Of(Owner).Role);
    }

    [Fact]
    public void Nobody_changes_their_own_role()
    {
        var error = Assert.Throws<ConflictException>(() => Group().ChangeRole(Owner, Owner, MemberRole.Member));

        Assert.Contains("someone else", error.Message);
        Assert.Equal(MemberRole.Owner, Of(Owner).Role);
    }

    [Fact]
    public void The_role_of_someone_who_is_not_in_the_group_cannot_be_changed()
    {
        Row(Id(10), MemberRole.Member, 4, left: true);
        var group = Group();

        Assert.Throws<NotFoundException>(() => group.ChangeRole(Owner, Outsider, MemberRole.Admin));
        Assert.Throws<NotFoundException>(() => group.ChangeRole(Owner, Id(10), MemberRole.Admin));
    }

    [Fact]
    public void Asking_for_the_role_someone_already_has_changes_nothing()
    {
        var outcome = Group().ChangeRole(Owner, Admin, MemberRole.Admin);

        Assert.True(outcome.IsEmpty);
        Assert.Equal(MemberRole.Admin, Of(Admin).Role);
    }

    [Fact]
    public void Someone_who_left_cannot_change_roles()
    {
        Row(Id(50), MemberRole.Owner, 4, left: true);

        Assert.Throws<ForbiddenException>(() => Group().ChangeRole(Id(50), Member, MemberRole.Admin));
    }

    // ##### Who may manage #####

    [Fact]
    public void The_owner_and_an_admin_may_manage_and_a_plain_member_may_not()
    {
        var group = Group();

        Assert.Equal(Owner, group.RequireManager(Owner, "rename this group").UserId);
        Assert.Equal(Admin, group.RequireManager(Admin, "rename this group").UserId);
        var refusal = Assert.Throws<ForbiddenException>(() => group.RequireManager(Member, "rename this group"));
        Assert.Equal("Only the owner or an admin can rename this group.", refusal.Message);
    }

    [Fact]
    public void The_outcome_lists_everyone_it_touched()
    {
        var outcome = new MembershipOutcome([Id(1)], [Id(2), Id(3)], [Id(4)]);

        Assert.Equal([Id(1), Id(2), Id(3), Id(4)], outcome.Affected);
        Assert.False(outcome.IsEmpty);
        Assert.True(MembershipOutcome.None.IsEmpty);
    }
}
