namespace ChatNET.API.Conversations.Models;

// Name is resolved per viewer: for a Direct conversation it is the other participant's
// display name, for a Group it is the group name. UnreadCount is per viewer too: the messages
// other people sent since the viewer last read the conversation, at most ConversationRepository's cap.
// OtherUserId is the other participant of a Direct conversation (what presence is shown for) and
// null for a Group. IsMuted is the viewer's own choice: it only quiets alerts for new messages, and what a
// conversation shows to its other members does not change. LeftAt is when the viewer stopped being a member of a group
// (they left, or were removed), or null while they are one: a conversation they left stays in their list, read-only,
// showing what was said up to that moment. KeyEpoch is the newest key period of the conversation (0 while it has none) and
// KeyRotationRequired says someone left since it was made, so the next one must be made before anything more is said.
public record ConversationDto(
    Guid Id,
    ConversationType Type,
    string Name,
    string? AvatarUrl,
    DateTime UpdatedAt,
    LastMessageDto? LastMessage,
    int UnreadCount,
    Guid? OtherUserId,
    bool IsMuted,
    DateTime? LeftAt,
    int KeyEpoch,
    bool KeyRotationRequired
);

// The last message as the list shows it, sealed as it was sent: the server cannot read it, so the browser opens it with the key of
// KeyEpoch to draw the preview, with the sender and the client message id and edit count it was signed for. Ciphertext is null when
// the last message was deleted. AttachmentCount says how many files the message carries (0 for a deleted message), so a message of
// only files can still be previewed.
public record LastMessageDto(
    Guid SenderId,
    string? Ciphertext,
    int KeyEpoch,
    Guid ClientMessageId,
    int EditCount,
    DateTime CreatedAt,
    int AttachmentCount
);
