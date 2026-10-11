using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Messaging.Persistence;

public interface IMessageRepository
{
    // Saves the message and moves its conversation's UpdatedAt to the message's CreatedAt, as one
    // atomic write: either both happen or neither does.
    Task AddAsync(Message message, CancellationToken cancellationToken);

    // The newest "take" messages of a conversation sent at or after "since", returned oldest first.
    // A deleted message is returned with its content removed, so the timeline has no gaps.
    Task<List<MessageDto>> GetLatestAsync(
        Guid conversationId, DateTime since, int take, CancellationToken cancellationToken);
}
