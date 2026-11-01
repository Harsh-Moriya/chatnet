using System.Collections.Concurrent;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Tests.Conversations;

// Test double that makes a hub call read membership as it was a moment ago. It wraps the real repository and answers the first
// "is this person an active member?" and the first "which conversations is this person in?" with what the test scripted,
// which is what the call would have read if the person had been removed a moment later than the read. Every other
// answer, and every answer after that, is the real one. It is how the two races of joining a group (a removal between the check and
// the join, and between the list and the joins) are made to happen on purpose.
public sealed class StaleConversationRepository : IConversationRepository
{
    private readonly IConversationRepository _inner;
    private readonly ConcurrentQueue<bool> _activeChecks;
    private readonly ConcurrentQueue<IReadOnlyList<Guid>> _activeLists;

    public StaleConversationRepository(
        IConversationRepository inner,
        ConcurrentQueue<bool> activeChecks,
        ConcurrentQueue<IReadOnlyList<Guid>> activeLists)
    {
        _inner = inner;
        _activeChecks = activeChecks;
        _activeLists = activeLists;
    }

    public Task<bool> IsActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        _activeChecks.TryDequeue(out var stale)
            ? Task.FromResult(stale)
            : _inner.IsActiveMemberAsync(conversationId, userId, cancellationToken);

    public async Task<List<Guid>> GetActiveConversationIdsForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        _activeLists.TryDequeue(out var stale)
            ? stale.ToList()
            : await _inner.GetActiveConversationIdsForUserAsync(userId, cancellationToken);

    public Task<Conversation?> GetDirectByKeyAsync(string directKey, CancellationToken cancellationToken) =>
        _inner.GetDirectByKeyAsync(directKey, cancellationToken);

    public Task<bool> TryAddDirectAsync(
        Conversation conversation, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken) =>
        _inner.TryAddDirectAsync(conversation, outbox, cancellationToken);

    public Task AddAsync(
        Conversation conversation, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken) =>
        _inner.AddAsync(conversation, notifications, outbox, cancellationToken);

    public Task<ConversationDto?> GetForViewerAsync(Guid conversationId, Guid viewerId, CancellationToken cancellationToken) =>
        _inner.GetForViewerAsync(conversationId, viewerId, cancellationToken);

    public Task<List<ConversationDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        _inner.GetForUserAsync(userId, cancellationToken);

    public Task<DateTime?> GetActiveMemberJoinedAtAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        _inner.GetActiveMemberJoinedAtAsync(conversationId, userId, cancellationToken);

    public Task<MemberWindow?> GetMemberWindowAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        _inner.GetMemberWindowAsync(conversationId, userId, cancellationToken);

    public Task<List<Guid>> GetActiveMemberIdsAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _inner.GetActiveMemberIdsAsync(conversationId, cancellationToken);

    public Task<MemberRole?> GetActiveMemberRoleAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        _inner.GetActiveMemberRoleAsync(conversationId, userId, cancellationToken);

    public Task<List<Guid>> GetContactIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        _inner.GetContactIdsAsync(userId, cancellationToken);

    public Task<bool> AdvanceLastReadAsync(
        Guid conversationId, Guid userId, DateTime readAt, OutboxMessage outbox, CancellationToken cancellationToken) =>
        _inner.AdvanceLastReadAsync(conversationId, userId, readAt, outbox, cancellationToken);

    public Task<List<MemberDto>> GetActiveMembersAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _inner.GetActiveMembersAsync(conversationId, cancellationToken);

    public Task<bool> SetMutedAsync(Guid conversationId, Guid userId, bool isMuted, CancellationToken cancellationToken) =>
        _inner.SetMutedAsync(conversationId, userId, isMuted, cancellationToken);

    public Task<DateTime?> GetLeftAtAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        _inner.GetLeftAtAsync(conversationId, userId, cancellationToken);

    public Task<ConversationUpdateDto?> GetUpdateAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _inner.GetUpdateAsync(conversationId, cancellationToken);

    public Task<IMembershipChange?> BeginMembershipChangeAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _inner.BeginMembershipChangeAsync(conversationId, cancellationToken);
}
