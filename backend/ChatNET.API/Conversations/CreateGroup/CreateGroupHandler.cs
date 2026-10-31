using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Notifications;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Conversations.CreateGroup;

public class CreateGroupHandler : IRequestHandler<CreateGroupCommand, ConversationDto>
{
    private readonly IConversationRepository _conversations;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<CreateGroupHandler> _logger;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly IHubContext<ChatHub, IChatClient> _hub;

    public CreateGroupHandler(
        IConversationRepository conversations,
        UserManager<AppUser> userManager,
        ILogger<CreateGroupHandler> logger,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        IHubContext<ChatHub, IChatClient> hub)
    {
        _conversations = conversations;
        _userManager = userManager;
        _logger = logger;
        _notifier = notifier;
        _outbox = outbox;
        _hub = hub;
    }

    public async Task<ConversationDto> Handle(CreateGroupCommand request, CancellationToken cancellationToken)
    {
        // The creator is added as Owner below, so they are removed from the invite list
        // and duplicates are collapsed before checking that everyone exists.
        var memberIds = request.MemberIds
            .Where(id => id != request.UserId)
            .Distinct()
            .ToList();

        if (memberIds.Count == 0)
            throw new ConflictException("A group needs at least one other member.");

        var validIds = await _userManager.Users
            .Where(u => memberIds.Contains(u.Id) && !u.IsDeactivated)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var missing = memberIds.Except(validIds).ToList();
        if (missing.Count > 0)
            throw new NotFoundException("User", string.Join(", ", missing));

        var now = DateTime.UtcNow;
        // The ids are made here, not by the database, because the outbox rows saved with the group name them.
        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            Type = ConversationType.Group,
            Name = request.Name.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedById = request.UserId,
            Members =
            [
                new ConversationMember { UserId = request.UserId, Role = MemberRole.Owner, JoinedAt = now },
                .. memberIds.Select(id => new ConversationMember
                {
                    UserId = id,
                    Role = MemberRole.Member,
                    JoinedAt = now
                })
            ]
        };

        // Each person who was added is told so, as a notification saved together with the group. The creator is not:
        // they know.
        var invitations = memberIds.Select(id => new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = id,
            ActorId = request.UserId,
            Type = NotificationType.MemberAdded,
            CreatedAt = now,
            Conversation = conversation,
        }).ToList();

        // The announcement of the group and each invitation are saved with it, so the pushes cannot be lost to a failure
        // after the commit: the relay makes any that this request does not.
        var allMemberIds = new List<Guid> { request.UserId };
        allMemberIds.AddRange(memberIds);
        var announcementRow = _outbox.Create(new ConversationCreated(conversation.Id, request.UserId, allMemberIds));
        var invitationRows = invitations
            .Select(invitation => _outbox.Create(new NotificationCreated(invitation.Id, invitation.UserId)))
            .ToList();

        await _conversations.AddAsync(conversation, invitations, [announcementRow, .. invitationRows], cancellationToken);

        _logger.LogInformation(
            "Group conversation {ConversationId} created by {UserId} with {MemberCount} members",
            conversation.Id, request.UserId, memberIds.Count + 1);

        var dto = await _conversations.GetForViewerAsync(conversation.Id, request.UserId, cancellationToken)
                  ?? throw new NotFoundException("Conversation", conversation.Id);

        await _outbox.DeliverAsync(
            announcementRow, () => _notifier.NotifyCreatedAsync(dto, request.UserId, allMemberIds, cancellationToken),
            cancellationToken);

        var creator = await _userManager.FindByIdAsync(request.UserId.ToString());
        for (var i = 0; i < invitations.Count; i++)
        {
            var invitation = invitations[i];
            var notification = new NotificationDto(
                invitation.Id, invitation.Type, invitation.ConversationId, invitation.ActorId,
                creator?.DisplayName ?? string.Empty, null, null, invitation.IsRead, invitation.CreatedAt);

            await _outbox.DeliverAsync(
                invitationRows[i],
                () => _hub.Clients.User(invitation.UserId.ToString()).NotificationReceived(notification),
                cancellationToken);
        }

        return dto;
    }
}
