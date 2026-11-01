using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.Membership;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;

namespace ChatNET.API.Conversations.LeaveConversation;

public class LeaveConversationHandler : IRequestHandler<LeaveConversationCommand, LeaveResultDto>
{
    private readonly IConversationRepository _conversations;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger<LeaveConversationHandler> _logger;

    public LeaveConversationHandler(
        IConversationRepository conversations,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        TimeProvider time,
        ILogger<LeaveConversationHandler> logger)
    {
        _conversations = conversations;
        _notifier = notifier;
        _outbox = outbox;
        _time = time;
        _logger = logger;
    }

    public async Task<LeaveResultDto> Handle(LeaveConversationCommand request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        MembershipOutcome outcome;
        OutboxMessage membersChanged;
        OutboxMessage memberLeft;

        await using (var change = await _conversations.BeginMembershipChangeAsync(request.ConversationId, cancellationToken)
                                  ?? throw new ForbiddenException(ConversationErrors.NotAMember))
        {
            change.RequireGroupMember(request.UserId);

            // When the owner leaves, the group is handed on in the same step, so the people left are told of both at once.
            outcome = change.Members.Leave(request.UserId, now);

            membersChanged = _outbox.Create(new MembersChanged(request.ConversationId, outcome.Affected));
            memberLeft = _outbox.Create(new MemberLeft(request.ConversationId, request.UserId));

            await change.CommitAsync([], [membersChanged, memberLeft], cancellationToken);
        }

        _logger.LogInformation(
            "User {UserId} left group {ConversationId}; {ChangedCount} members changed role",
            request.UserId, request.ConversationId, outcome.Changed.Count);

        await _outbox.DeliverAsync(
            memberLeft,
            () => _notifier.NotifyLeftAsync(request.ConversationId, request.UserId, cancellationToken),
            cancellationToken);

        await _outbox.DeliverAsync(
            membersChanged,
            () => _notifier.NotifyMembersChangedAsync(request.ConversationId, outcome.Affected.ToList(), cancellationToken),
            cancellationToken);

        return new LeaveResultDto(now);
    }
}
