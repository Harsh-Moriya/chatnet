using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;

namespace ChatNET.API.Tests.Messaging;

// Test double that forces a race the real code only hits by chance. It wraps the real repository and
// answers the first "have I seen this client id?" lookup of a request with "no", whatever is stored. The
// request then goes on to insert, and the unique index has to be the one to stop it: this is what a copy of
// a send does when another copy was stored in the moment between its lookup and its insert.
//
// Only the first lookup is forgotten, so the lookup after the lost insert (which fetches the winner's
// message) sees the truth.
public sealed class ForgetfulMessageRepository : IMessageRepository
{
    private readonly IMessageRepository _inner;
    private bool _forgotten;

    public ForgetfulMessageRepository(IMessageRepository inner)
    {
        _inner = inner;
    }

    public async Task<MessageDto?> FindByClientMessageIdAsync(
        Guid conversationId, Guid senderId, Guid clientMessageId, CancellationToken cancellationToken)
    {
        if (!_forgotten)
        {
            _forgotten = true;
            return null;
        }

        return await _inner.FindByClientMessageIdAsync(conversationId, senderId, clientMessageId, cancellationToken);
    }

    public Task<bool> TryAddAsync(Message message, CancellationToken cancellationToken)
        => _inner.TryAddAsync(message, cancellationToken);

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
