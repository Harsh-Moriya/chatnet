namespace ChatNET.API.Conversations.Models;

// The part of a conversation a person may read: what was sent from the moment they joined to the moment they left, or up
// to now while they are still in it. Being added to a group does not reveal what was said before, and leaving it does not
// reveal what is said after.
public record MemberWindow(DateTime JoinedAt, DateTime? LeftAt);
