using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;

namespace ChatNET.API.Tests.Media;

// Test double for a database that fails at the moment a message is saved. Everything else is the real
// repository. The send has by then copied the files to their permanent place, and has to take the copies back.
public sealed class ThrowingMessageRepository : IMessageRepository
{
    private readonly IMessageRepository _inner;

    public ThrowingMessageRepository(IMessageRepository inner)
    {
        _inner = inner;
    }

    public Task<bool> TryAddAsync(Message message, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The database refused the message.");

    public Task<MessageDto?> FindByClientMessageIdAsync(
        Guid conversationId, Guid senderId, Guid clientMessageId, CancellationToken cancellationToken)
        => _inner.FindByClientMessageIdAsync(conversationId, senderId, clientMessageId, cancellationToken);

    public Task<MessageCursor?> FindCursorAsync(
        Guid conversationId, Guid messageId, DateTime since, CancellationToken cancellationToken)
        => _inner.FindCursorAsync(conversationId, messageId, since, cancellationToken);

    public Task<List<MessageDto>> GetLatestAsync(
        Guid conversationId, DateTime since, int take, CancellationToken cancellationToken)
        => _inner.GetLatestAsync(conversationId, since, take, cancellationToken);

    public Task<List<MessageDto>> GetBeforeAsync(
        Guid conversationId, DateTime since, MessageCursor cursor, int take, CancellationToken cancellationToken)
        => _inner.GetBeforeAsync(conversationId, since, cursor, take, cancellationToken);

    public Task<List<MessageDto>> GetAfterAsync(
        Guid conversationId, DateTime since, MessageCursor cursor, int take, CancellationToken cancellationToken)
        => _inner.GetAfterAsync(conversationId, since, cursor, take, cancellationToken);
}
