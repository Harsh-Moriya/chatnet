using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.Membership;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Conversations.AddMembers;

public class AddMembersHandler : IRequestHandler<AddMembersCommand, List<MemberDto>>
{
    private readonly IConversationRepository _conversations;
    private readonly UserManager<AppUser> _userManager;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<AddMembersHandler> _logger;

    public AddMembersHandler(
        IConversationRepository conversations,
        UserManager<AppUser> userManager,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        IHubContext<ChatHub, IChatClient> hub,
        TimeProvider time,
        ILogger<AddMembersHandler> logger)
    {
        _conversations = conversations;
        _userManager = userManager;
        _notifier = notifier;
        _outbox = outbox;
        _hub = hub;
        _time = time;
        _logger = logger;
    }

    public async Task<List<MemberDto>> Handle(AddMembersCommand request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        MembershipOutcome outcome;
        List<Notification> invitations;
        List<OutboxMessage> rows;

        await using (var change = await _conversations.BeginMembershipChangeAsync(request.ConversationId, cancellationToken)
                                  ?? throw new ForbiddenException(ConversationErrors.NotAMember))
        {
            change.RequireGroupMember(request.ActorId);

            // Before anything is looked up about the people named, so someone who may not add anyone learns nothing about
            // which accounts exist.
            change.Members.RequireManager(request.ActorId, "add people to this group");

            var requested = request.UserIds.Distinct().ToList();
            var valid = await _userManager.Users
                .Where(u => requested.Contains(u.Id) && !u.IsDeactivated)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            var missing = requested.Except(valid).ToList();
            if (missing.Count > 0)
                throw new NotFoundException("User", string.Join(", ", missing));

            outcome = change.Members.Add(request.ActorId, requested, now);

            // Each person who was added is told so in the bell, saved with the change, as when a group is made.
            invitations = outcome.Joined.Select(userId => new Notification
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                ActorId = request.ActorId,
                Type = NotificationType.MemberAdded,
                CreatedAt = now,
                Conversation = change.Conversation,
            }).ToList();

            rows =
            [
                _outbox.Create(new MembersChanged(request.ConversationId, outcome.Joined)),
                _outbox.Create(new MembersJoined(request.ConversationId, outcome.Joined)),
                .. invitations.Select(n => _outbox.Create(new NotificationCreated(n.Id, n.UserId))),
            ];

            await change.CommitAsync(invitations, rows, cancellationToken);
        }

        _logger.LogInformation(
            "User {UserId} added {AddedCount} people to group {ConversationId}",
            request.ActorId, outcome.Joined.Count, request.ConversationId);

        await _outbox.DeliverAsync(
            rows[0],
            () => _notifier.NotifyMembersChangedAsync(request.ConversationId, outcome.Joined.ToList(), cancellationToken),
            cancellationToken);

        await _outbox.DeliverAsync(
            rows[1],
            () => _notifier.NotifyJoinedAsync(request.ConversationId, outcome.Joined.ToList(), cancellationToken),
            cancellationToken);

        var actor = await _userManager.FindByIdAsync(request.ActorId.ToString());
        for (var i = 0; i < invitations.Count; i++)
        {
            var invitation = invitations[i];
            var notification = new NotificationDto(
                invitation.Id, invitation.Type, invitation.ConversationId, invitation.ActorId,
                actor?.DisplayName ?? string.Empty, null, null, invitation.IsRead, invitation.CreatedAt);

            await _outbox.DeliverAsync(
                rows[2 + i],
                () => _hub.Clients.User(invitation.UserId.ToString()).NotificationReceived(notification),
                cancellationToken);
        }

        return await _conversations.GetActiveMembersAsync(request.ConversationId, cancellationToken);
    }
}
