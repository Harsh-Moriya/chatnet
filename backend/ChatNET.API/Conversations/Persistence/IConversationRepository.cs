using ChatNET.API.Conversations.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Conversations.Persistence;

public interface IConversationRepository
{
    Task<Conversation?> GetDirectByKeyAsync(string directKey, CancellationToken cancellationToken);

    // Persists a new Direct conversation, with the outbox rows for its announcement. Returns false (saving nothing,
    // rows included) if the DirectKey unique index rejected it because the same pair of users already has a
    // conversation (a race between two concurrent creates).
    Task<bool> TryAddDirectAsync(
        Conversation conversation, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken);

    // Saves the conversation together with the notifications that come with it and the outbox rows for their
    // pushes, in one transaction.
    Task AddAsync(
        Conversation conversation, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken);

    Task<ConversationDto?> GetForViewerAsync(Guid conversationId, Guid viewerId, CancellationToken cancellationToken);

    Task<List<ConversationDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> IsActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken);

    // When the user joined, if they are an active member; null if they are not in the conversation
    // or have left it. Doubles as the membership check for features that also need the join time.
    Task<DateTime?> GetActiveMemberJoinedAtAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken);

    Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<List<Guid>> GetActiveConversationIdsForUserAsync(Guid userId, CancellationToken cancellationToken);

    // Everyone who shares an active conversation with this user: the people who may see their presence,
    // and whose presence they may see. Never includes the user, never a member who left.
    Task<List<Guid>> GetContactIdsAsync(Guid userId, CancellationToken cancellationToken);

    // Moves an active member's read position to readAt, only if that is later than where it is.
    // Returns whether it moved, so callers announce a change only when there was one. The outbox row for that
    // announcement is saved in the same transaction as the move, and only when it moved.
    Task<bool> AdvanceLastReadAsync(
        Guid conversationId, Guid userId, DateTime readAt, OutboxMessage outbox, CancellationToken cancellationToken);

    // The active members, earliest joiner first, each with how far they have read.
    Task<List<MemberDto>> GetActiveMembersAsync(Guid conversationId, CancellationToken cancellationToken);

    // Stores whether an active member has muted the conversation. Returns false when the user is not an active
    // member, in which case nothing was changed.
    Task<bool> SetMutedAsync(Guid conversationId, Guid userId, bool isMuted, CancellationToken cancellationToken);
}
