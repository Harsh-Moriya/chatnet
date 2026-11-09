using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Conversations.Persistence;

public interface IKeyEpochRepository
{
    // The epochs from `fromEpoch` that this person has an envelope for, oldest first, with their own envelope and the people each epoch
    // was made for. Asks for one more than `limit` so the caller can tell whether there are more. Nobody else's envelope is read.
    Task<List<KeyEpochDto>> GetForMemberAsync(
        Guid conversationId, Guid userId, int fromEpoch, int take, CancellationToken cancellationToken);

    // When the epoch was made, or null when the conversation has no such epoch.
    Task<DateTime?> GetCreatedAtAsync(Guid conversationId, int epoch, CancellationToken cancellationToken);
}
