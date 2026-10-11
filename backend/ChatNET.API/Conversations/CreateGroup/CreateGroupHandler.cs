using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Conversations.CreateGroup;

public class CreateGroupHandler : IRequestHandler<CreateGroupCommand, ConversationDto>
{
    private readonly IConversationRepository _conversations;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<CreateGroupHandler> _logger;
    private readonly ConversationNotifier _notifier;

    public CreateGroupHandler(
        IConversationRepository conversations,
        UserManager<AppUser> userManager,
        ILogger<CreateGroupHandler> logger,
        ConversationNotifier notifier)
    {
        _conversations = conversations;
        _userManager = userManager;
        _logger = logger;
        _notifier = notifier;
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
        var conversation = new Conversation
        {
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

        await _conversations.AddAsync(conversation, cancellationToken);

        _logger.LogInformation(
            "Group conversation {ConversationId} created by {UserId} with {MemberCount} members",
            conversation.Id, request.UserId, memberIds.Count + 1);

        var dto = await _conversations.GetForViewerAsync(conversation.Id, request.UserId, cancellationToken)
                  ?? throw new NotFoundException("Conversation", conversation.Id);

        await _notifier.NotifyCreatedAsync(dto, request.UserId, [request.UserId, .. memberIds], cancellationToken);

        return dto;
    }
}
