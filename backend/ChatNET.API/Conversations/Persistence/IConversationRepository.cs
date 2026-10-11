using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Conversations.Persistence;

public interface IConversationRepository
{
    Task<Conversation?> GetDirectByKeyAsync(string directKey, CancellationToken cancellationToken);

    // Persists a new Direct conversation. Returns false if the DirectKey unique index
    // rejected it because the same pair of users already has a conversation (a race
    // between two concurrent creates).
    Task<bool> TryAddDirectAsync(Conversation conversation, CancellationToken cancellationToken);

    Task AddAsync(Conversation conversation, CancellationToken cancellationToken);

    Task<ConversationDto?> GetForViewerAsync(Guid conversationId, Guid viewerId, CancellationToken cancellationToken);

    Task<List<ConversationDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> IsActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken);

    Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<List<Guid>> GetActiveConversationIdsForUserAsync(Guid userId, CancellationToken cancellationToken);
}
