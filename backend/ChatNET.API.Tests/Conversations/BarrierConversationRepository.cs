using System.Collections.Concurrent;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;

namespace ChatNET.API.Tests.Conversations;

// Test double that forces a race the real code only hits by chance. It wraps the real repository
// and, after a request has looked up the direct conversation and found nothing, holds that
// request at a barrier until a second request has done the same. Both then try to insert, so
// exactly one of them must lose to the unique index on DirectKey.
//
// The barrier applies only to the first lookup of each request, so the loser's second lookup
// (the re-read of the winner's row) is not held up.
public sealed class BarrierConversationRepository : IConversationRepository
{
    private readonly IConversationRepository _inner;
    private readonly Barrier _barrier;
    private readonly ConcurrentBag<bool> _insertResults;
    private bool _passedBarrier;

    public BarrierConversationRepository(
        IConversationRepository inner, Barrier barrier, ConcurrentBag<bool> insertResults)
    {
        _inner = inner;
        _barrier = barrier;
        _insertResults = insertResults;
    }

    public async Task<Conversation?> GetDirectByKeyAsync(string directKey, CancellationToken cancellationToken)
    {
        var found = await _inner.GetDirectByKeyAsync(directKey, cancellationToken);

        if (!_passedBarrier)
        {
            _passedBarrier = true;

            // A timeout means the second request never arrived; the test then fails on its own
            // assertions instead of hanging.
            _barrier.SignalAndWait(TimeSpan.FromSeconds(15));
        }

        return found;
    }

    public async Task<bool> TryAddDirectAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        var added = await _inner.TryAddDirectAsync(conversation, cancellationToken);
        _insertResults.Add(added);
        return added;
    }

    public Task AddAsync(Conversation conversation, CancellationToken cancellationToken) =>
        _inner.AddAsync(conversation, cancellationToken);

    public Task<ConversationDto?> GetForViewerAsync(Guid conversationId, Guid viewerId, CancellationToken cancellationToken) =>
        _inner.GetForViewerAsync(conversationId, viewerId, cancellationToken);

    public Task<List<ConversationDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        _inner.GetForUserAsync(userId, cancellationToken);

    public Task<bool> IsActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        _inner.IsActiveMemberAsync(conversationId, userId, cancellationToken);

    public Task<DateTime?> GetActiveMemberJoinedAtAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        _inner.GetActiveMemberJoinedAtAsync(conversationId, userId, cancellationToken);

    public Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _inner.GetActiveMemberIdsAsync(conversationId, cancellationToken);

    public Task<List<Guid>> GetActiveConversationIdsForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        _inner.GetActiveConversationIdsForUserAsync(userId, cancellationToken);

    public Task<List<Guid>> GetContactIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        _inner.GetContactIdsAsync(userId, cancellationToken);

    public Task<bool> AdvanceLastReadAsync(
        Guid conversationId, Guid userId, DateTime readAt, CancellationToken cancellationToken) =>
        _inner.AdvanceLastReadAsync(conversationId, userId, readAt, cancellationToken);

    public Task<List<MemberDto>> GetActiveMembersAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _inner.GetActiveMembersAsync(conversationId, cancellationToken);

    public Task<bool> SetMutedAsync(Guid conversationId, Guid userId, bool isMuted, CancellationToken cancellationToken) =>
        _inner.SetMutedAsync(conversationId, userId, isMuted, cancellationToken);
}
