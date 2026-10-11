using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Messaging.Persistence;

public interface IMessageRepository
{
    // Saves the message and moves its conversation's UpdatedAt to the message's CreatedAt, as one
    // atomic write: either both happen or neither does.
    Task AddAsync(Message message, CancellationToken cancellationToken);
}
