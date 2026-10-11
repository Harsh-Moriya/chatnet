namespace ChatNET.API.Conversations.Models;

// Name is resolved per viewer: for a Direct conversation it is the other participant's
// display name, for a Group it is the group name.
public record ConversationDto(
    Guid Id,
    ConversationType Type,
    string Name,
    string? AvatarUrl,
    DateTime UpdatedAt,
    LastMessageDto? LastMessage
);

// Content is null when the last message was deleted. It is truncated to a preview length.
public record LastMessageDto(
    Guid SenderId,
    string? Content,
    DateTime CreatedAt
);
