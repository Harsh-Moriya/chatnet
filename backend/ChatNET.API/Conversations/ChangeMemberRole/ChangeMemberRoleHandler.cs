using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Membership;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;

namespace ChatNET.API.Conversations.ChangeMemberRole;

public class ChangeMemberRoleHandler : IRequestHandler<ChangeMemberRoleCommand, List<MemberDto>>
{
    private readonly IConversationRepository _conversations;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly ILogger<ChangeMemberRoleHandler> _logger;

    public ChangeMemberRoleHandler(
        IConversationRepository conversations,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        ILogger<ChangeMemberRoleHandler> logger)
    {
        _conversations = conversations;
        _notifier = notifier;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<List<MemberDto>> Handle(ChangeMemberRoleCommand request, CancellationToken cancellationToken)
    {
        // The validator has refused a missing role.
        var role = request.Role!.Value;

        MembershipOutcome outcome;
        OutboxMessage? row = null;

        await using (var change = await _conversations.BeginMembershipChangeAsync(request.ConversationId, cancellationToken)
                                  ?? throw new ForbiddenException(ConversationErrors.NotAMember))
        {
            change.RequireGroupMember(request.ActorId);
            outcome = change.Members.ChangeRole(request.ActorId, request.TargetId, role);

            // Asking for the role someone already has stores and says nothing.
            if (!outcome.IsEmpty)
            {
                row = _outbox.Create(new MembersChanged(request.ConversationId, outcome.Changed));
                await change.CommitAsync([], [row], cancellationToken);
            }
        }

        if (row is not null)
        {
            _logger.LogInformation(
                "User {UserId} made user {TargetId} {Role} in group {ConversationId}",
                request.ActorId, request.TargetId, role, request.ConversationId);

            await _outbox.DeliverAsync(
                row,
                () => _notifier.NotifyMembersChangedAsync(request.ConversationId, outcome.Changed.ToList(), cancellationToken),
                cancellationToken);
        }

        return await _conversations.GetActiveMembersAsync(request.ConversationId, cancellationToken);
    }
}
