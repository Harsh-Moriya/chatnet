using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Conversations.CreateDirect;

// Find-or-create: calling this twice for the same pair of users returns the same
// conversation, so the client can call it every time a user clicks "message" on a profile.
public class CreateDirectHandler : IRequestHandler<CreateDirectCommand, ConversationDto>
{
    private readonly IConversationRepository _conversations;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<CreateDirectHandler> _logger;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;

    public CreateDirectHandler(
        IConversationRepository conversations,
        UserManager<AppUser> userManager,
        ILogger<CreateDirectHandler> logger,
        ConversationNotifier notifier,
        OutboxPublisher outbox)
    {
        _conversations = conversations;
        _userManager = userManager;
        _logger = logger;
        _notifier = notifier;
        _outbox = outbox;
    }

    public async Task<ConversationDto> Handle(CreateDirectCommand request, CancellationToken cancellationToken)
    {
        var other = await _userManager.FindByIdAsync(request.OtherUserId.ToString());
        if (other is null || other.IsDeactivated)
            throw new NotFoundException("User", request.OtherUserId);

        var directKey = DirectKey.For(request.UserId, request.OtherUserId);

        var conversation = await _conversations.GetDirectByKeyAsync(directKey, cancellationToken);
        OutboxMessage? announcement = null;

        if (conversation is null)
        {
            var now = DateTime.UtcNow;
            var created = new Conversation
            {
                Id = Guid.CreateVersion7(),
                Type = ConversationType.Direct,
                DirectKey = directKey,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedById = request.UserId,
                Members =
                [
                    new ConversationMember { UserId = request.UserId, Role = MemberRole.Member, JoinedAt = now },
                    new ConversationMember { UserId = request.OtherUserId, Role = MemberRole.Member, JoinedAt = now },
                ]
            };

            // The announcement is saved with the conversation: if the insert loses a race, it is not saved either.
            var row = _outbox.Create(new ConversationCreated(
                created.Id, request.UserId, [request.UserId, request.OtherUserId]));

            if (await _conversations.TryAddDirectAsync(created, [row], cancellationToken))
            {
                conversation = created;
                announcement = row;
                _logger.LogInformation(
                    "Direct conversation {ConversationId} created by {UserId}", created.Id, request.UserId);
            }
            else
            {
                // Lost a race with a concurrent create for the same pair: the unique index
                // rejected our insert, so the winner's row is the one to return.
                conversation = await _conversations.GetDirectByKeyAsync(directKey, cancellationToken)
                               ?? throw new ConflictException("Could not create the conversation. Please try again.");
            }
        }

        var dto = await _conversations.GetForViewerAsync(conversation.Id, request.UserId, cancellationToken)
                  ?? throw new NotFoundException("Conversation", conversation.Id);

        // Only the request that created the conversation announces it. A repeated call finds the
        // existing one, and a request that lost a race returns the winner's, which the winner
        // announces.
        if (announcement is not null)
        {
            await _outbox.DeliverAsync(
                announcement,
                () => _notifier.NotifyCreatedAsync(dto, request.UserId, [request.UserId, request.OtherUserId], cancellationToken),
                cancellationToken);
        }

        return dto;
    }
}
