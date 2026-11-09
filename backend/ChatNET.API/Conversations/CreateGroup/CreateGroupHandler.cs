using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Keys.Persistence;
using ChatNET.API.Messaging;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Notifications;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Push;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Conversations.CreateGroup;

public class CreateGroupHandler : IRequestHandler<CreateGroupCommand, ConversationDto>
{
    private readonly IConversationRepository _conversations;
    private readonly IKeyRepository _keys;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<CreateGroupHandler> _logger;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly PushAlertRows _alerts;
    private readonly IHubContext<ChatHub, IChatClient> _hub;

    public CreateGroupHandler(
        IConversationRepository conversations,
        IKeyRepository keys,
        UserManager<AppUser> userManager,
        ILogger<CreateGroupHandler> logger,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        PushAlertRows alerts,
        IHubContext<ChatHub, IChatClient> hub)
    {
        _conversations = conversations;
        _keys = keys;
        _userManager = userManager;
        _logger = logger;
        _notifier = notifier;
        _outbox = outbox;
        _alerts = alerts;
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

        // The first key period must be for the creator and everyone invited, each at the key they have now.
        var allMemberIds = new List<Guid> { request.UserId };
        allMemberIds.AddRange(memberIds);
        var submission = EpochSubmission.From(request.Epoch);
        EpochRules.Check(0, allMemberIds, await _keys.GetCurrentGenerationsAsync(allMemberIds, cancellationToken), submission);

        var now = DateTime.UtcNow;
        // The id is the one the browser chose, because the first key period was made for it, and the outbox rows saved with the
        // group name it.
        var conversation = new Conversation
        {
            Id = request.Id,
            Type = ConversationType.Group,
            Name = request.Name.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedById = request.UserId,
            KeyEpoch = submission.Epoch,
            KeyEpochs = [submission.ToEntity(request.Id, request.UserId, KeyEpochReason.Created, now)],
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
        var announcementRow = _outbox.Create(new ConversationCreated(conversation.Id, request.UserId, allMemberIds));
        var invitationRows = invitations
            .Select(invitation => _outbox.Create(new NotificationCreated(invitation.Id, invitation.UserId)))
            .ToList();

        try
        {
            await _conversations.AddAsync(conversation, invitations, [announcementRow, .. invitationRows, .. _alerts.ForNotifications(invitations)], cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Only an id that is already a conversation's can be refused here: the people are distinct and checked already.
            throw new ConflictException("Could not create the conversation. Please try again.");
        }

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
