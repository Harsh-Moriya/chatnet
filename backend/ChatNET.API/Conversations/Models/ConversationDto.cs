namespace ChatNET.API.Conversations.Models;

// Name is resolved per viewer: for a Direct conversation it is the other participant's
// display name, for a Group it is the group name. UnreadCount is per viewer too: the messages
// other people sent since the viewer last read the conversation, at most ConversationRepository's cap.
// OtherUserId is the other participant of a Direct conversation (what presence is shown for) and
// null for a Group. IsMuted is the viewer's own choice: it only quiets alerts for new messages, and what a
// conversation shows to its other members does not change.
public record ConversationDto(
    Guid Id,
    ConversationType Type,
    string Name,
    string? AvatarUrl,
    DateTime UpdatedAt,
    LastMessageDto? LastMessage,
    int UnreadCount,
    Guid? OtherUserId,
    bool IsMuted
);

// Content is null when the last message was deleted. It is truncated to a preview length, and empty when the
// message is only files, which AttachmentCount is for: it says how many files the message carries (0 for a
// deleted message).
public record LastMessageDto(
    Guid SenderId,
    string? Content,
    DateTime CreatedAt,
    int AttachmentCount
);
