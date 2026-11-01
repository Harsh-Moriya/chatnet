using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;

namespace ChatNET.API.Conversations.RemoveMember;

public class RemoveMemberHandler : IRequestHandler<RemoveMemberCommand, List<MemberDto>>
{
    private readonly IConversationRepository _conversations;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoveMemberHandler> _logger;

    public RemoveMemberHandler(
        IConversationRepository conversations,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        TimeProvider time,
        ILogger<RemoveMemberHandler> logger)
    {
        _conversations = conversations;
        _notifier = notifier;
        _outbox = outbox;
        _time = time;
        _logger = logger;
    }

    public async Task<List<MemberDto>> Handle(RemoveMemberCommand request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        OutboxMessage membersChanged;
        OutboxMessage memberLeft;

        await using (var change = await _conversations.BeginMembershipChangeAsync(request.ConversationId, cancellationToken)
                                  ?? throw new ForbiddenException(ConversationErrors.NotAMember))
        {
            change.RequireGroupMember(request.ActorId);
            change.Members.Remove(request.ActorId, request.TargetId, now);

            membersChanged = _outbox.Create(new MembersChanged(request.ConversationId, [request.TargetId]));
            memberLeft = _outbox.Create(new MemberLeft(request.ConversationId, request.TargetId));

            await change.CommitAsync([], [membersChanged, memberLeft], cancellationToken);
        }

        _logger.LogInformation(
            "User {UserId} removed user {TargetId} from group {ConversationId}",
            request.ActorId, request.TargetId, request.ConversationId);

        await _outbox.DeliverAsync(
            memberLeft,
            () => _notifier.NotifyLeftAsync(request.ConversationId, request.TargetId, cancellationToken),
            cancellationToken);

        await _outbox.DeliverAsync(
            membersChanged,
            () => _notifier.NotifyMembersChangedAsync(request.ConversationId, [request.TargetId], cancellationToken),
            cancellationToken);

        return await _conversations.GetActiveMembersAsync(request.ConversationId, cancellationToken);
    }
}
