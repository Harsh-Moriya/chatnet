namespace ChatNET.API.Conversations.Models;

// UserName is the name a message addresses the member by ("@name"). LastReadAt is how far the member has read: they
// have seen every message sent at or before it. Null until they have read or sent something.
public record MemberDto(
    Guid UserId,
    string UserName,
    string DisplayName,
    string? AvatarUrl,
    MemberRole Role,
    DateTime? LastReadAt
);
