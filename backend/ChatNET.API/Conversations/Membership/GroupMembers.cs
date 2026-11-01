using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Conversations.Membership;

// The rules of who may do what to the membership of a group, applied to the rows of one conversation (all of them, the
// ones who left included). It changes the rows it is given and says who was touched. Saving them, and doing so while
// nobody else changes the same group, is the job of whoever loaded them (IMembershipChange).
//
// While anyone is in a group it has exactly one owner. The owner can do everything; an admin can add people, remove
// plain members and rename the group; a member can only leave. Every method checks the actor before it changes
// anything, so a refused call leaves the rows as they were.
public sealed class GroupMembers
{
    private readonly Guid _conversationId;
    private readonly ICollection<ConversationMember> _rows;
    private readonly Dictionary<Guid, ConversationMember> _byUser;

    public GroupMembers(Guid conversationId, ICollection<ConversationMember> rows)
    {
        _conversationId = conversationId;
        _rows = rows;
        _byUser = rows.ToDictionary(m => m.UserId);
    }

    public int ActiveCount => _rows.Count(m => m.LeftAt is null);

    // ##### Who may do what #####

    // The row of an active member of the group. Anyone else, whether they never were in it or have left, gets the one
    // refusal every endpoint gives for a conversation that is not theirs.
    public ConversationMember RequireMember(Guid userId)
        => FindActive(userId) ?? throw new ForbiddenException(ConversationErrors.NotAMember);

    // The actor as an active member who is the owner or an admin. Anyone else is refused: someone who is not in the group
    // gets the one answer every endpoint gives, a plain member is told what they may not do.
    public ConversationMember RequireManager(Guid actorId, string what)
    {
        var actor = RequireMember(actorId);

        if (actor.Role == MemberRole.Member)
            throw new ForbiddenException($"Only the owner or an admin can {what}.");

        return actor;
    }

    // ##### Adding #####

    // Adds people who are not in the group. Someone who left is back as a plain member with a new join time, so they see
    // nothing from before they left or from the time they were away. People already in the group are left out, and it
    // is an error only when that leaves nobody to add.
    public MembershipOutcome Add(Guid actorId, IReadOnlyCollection<Guid> userIds, DateTime now)
    {
        RequireManager(actorId, "add people to this group");

        var toAdd = userIds
            .Distinct()
            .Where(id => FindActive(id) is null)
            .ToList();

        if (toAdd.Count == 0)
            throw new ConflictException("Everyone you named is already in the group.");

        if (ActiveCount + toAdd.Count > GroupLimits.MaxActiveMembers)
            throw new ConflictException($"A group can have at most {GroupLimits.MaxActiveMembers} members.");

        foreach (var id in toAdd)
        {
            if (_byUser.TryGetValue(id, out var returning))
            {
                returning.LeftAt = null;
                returning.JoinedAt = now;
                returning.Role = MemberRole.Member;
                returning.LastReadAt = null;
                continue;
            }

            var added = new ConversationMember
            {
                ConversationId = _conversationId,
                UserId = id,
                Role = MemberRole.Member,
                JoinedAt = now,
            };
            _rows.Add(added);
            _byUser[id] = added;
        }

        return new MembershipOutcome(toAdd, [], []);
    }

    // ##### Removing and leaving #####

    // The owner removes anyone, an admin removes plain members. Removing oneself is leaving, which has its own rule
    // for what happens to the group.
    public MembershipOutcome Remove(Guid actorId, Guid targetId, DateTime now)
    {
        var actor = RequireManager(actorId, "remove people from this group");

        if (targetId == actorId)
            throw new ConflictException("You cannot remove yourself; leave the group instead.");

        var target = FindActive(targetId) ?? throw new NotFoundException("Member", targetId);

        if (actor.Role == MemberRole.Admin && target.Role != MemberRole.Member)
            throw new ForbiddenException("An admin can remove members, not other admins or the owner.");

        target.LeftAt = now;

        return new MembershipOutcome([], [], [targetId]);
    }

    // Anyone in the group may leave. When the owner does, the group is handed on in the same step: to the admin who has
    // been in it longest, else the member who has been in it longest. A group nobody is left in has no owner, and keeps
    // its history for the people who were in it.
    public MembershipOutcome Leave(Guid actorId, DateTime now)
    {
        var leaving = RequireMember(actorId);
        leaving.LeftAt = now;

        if (leaving.Role != MemberRole.Owner)
            return new MembershipOutcome([], [], [actorId]);

        var successor = _rows
            .Where(m => m.LeftAt is null)
            .OrderBy(m => m.Role == MemberRole.Admin ? 0 : 1)
            .ThenBy(m => m.JoinedAt)
            .ThenBy(m => m.UserId)
            .FirstOrDefault();

        if (successor is null)
            return new MembershipOutcome([], [], [actorId]);

        successor.Role = MemberRole.Owner;

        return new MembershipOutcome([], [successor.UserId], [actorId]);
    }

    // ##### Roles #####

    // Only the owner changes roles. Making someone the owner hands the group over: the owner becomes an admin, so there
    // is still exactly one owner. Asking for the role someone already has changes nothing.
    public MembershipOutcome ChangeRole(Guid actorId, Guid targetId, MemberRole role)
    {
        var actor = RequireMember(actorId);

        if (actor.Role != MemberRole.Owner)
            throw new ForbiddenException("Only the owner can change roles.");

        if (targetId == actorId)
            throw new ConflictException("You cannot change your own role; make someone else the owner instead.");

        var target = FindActive(targetId) ?? throw new NotFoundException("Member", targetId);

        if (target.Role == role)
            return MembershipOutcome.None;

        target.Role = role;

        if (role != MemberRole.Owner)
            return new MembershipOutcome([], [targetId], []);

        actor.Role = MemberRole.Admin;

        return new MembershipOutcome([], [targetId, actorId], []);
    }

    private ConversationMember? FindActive(Guid userId)
        => _byUser.TryGetValue(userId, out var row) && row.LeftAt is null ? row : null;
}
