namespace ChatNET.API.Conversations.Models;

// What changed about the people of a group, as it is pushed to the people who are in it: the members whose entry is new or
// changed, as it is now, and the people who are no longer in the group. It is built from the stored rows when it is sent, so
// applying it twice, or after a later change, ends in the same list.
public record MembersChangedDto(Guid ConversationId, List<MemberDto> Members, List<Guid> RemovedUserIds);

// The parts of a group that its members share and any owner or admin may change. Not a ConversationDto, which holds what is
// true of one viewer (their unread count, whether they muted it) and so cannot be sent to a whole group.
public record ConversationUpdateDto(Guid Id, string Name, string? AvatarUrl);

// The answer to leaving a group: the moment the person stopped being a member, as stored.
public record LeaveResultDto(DateTime LeftAt);
