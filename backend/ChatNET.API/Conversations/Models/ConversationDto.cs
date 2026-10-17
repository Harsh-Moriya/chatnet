namespace ChatNET.API.Conversations.Models;

// Name is resolved per viewer: for a Direct conversation it is the other participant's
// display name, for a Group it is the group name. UnreadCount is per viewer too: the messages
// other people sent since the viewer last read the conversation, at most ConversationRepository's cap.
// OtherUserId is the other participant of a Direct conversation (what presence is shown for) and
// null for a Group.
public record ConversationDto(
    Guid Id,
    ConversationType Type,
    string Name,
    string? AvatarUrl,
    DateTime UpdatedAt,
    LastMessageDto? LastMessage,
    int UnreadCount,
    Guid? OtherUserId
);

// Content is null when the last message was deleted. It is truncated to a preview length.
public record LastMessageDto(
    Guid SenderId,
    string? Content,
    DateTime CreatedAt
);
