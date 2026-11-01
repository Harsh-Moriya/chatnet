using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;

namespace ChatNET.API.Conversations.RenameConversation;

public class RenameConversationHandler : IRequestHandler<RenameConversationCommand, ConversationUpdateDto>
{
    private readonly IConversationRepository _conversations;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly ILogger<RenameConversationHandler> _logger;

    public RenameConversationHandler(
        IConversationRepository conversations,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        ILogger<RenameConversationHandler> logger)
    {
        _conversations = conversations;
        _notifier = notifier;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<ConversationUpdateDto> Handle(RenameConversationCommand request, CancellationToken cancellationToken)
    {
        // The validator has refused a missing or blank name.
        var name = request.Name!.Trim();

        ConversationUpdateDto update;
        OutboxMessage? row = null;

        await using (var change = await _conversations.BeginMembershipChangeAsync(request.ConversationId, cancellationToken)
                                  ?? throw new ForbiddenException(ConversationErrors.NotAMember))
        {
            change.RequireGroupMember(request.ActorId);
            change.Members.RequireManager(request.ActorId, "rename this group");

            // The same name again stores and says nothing, so repeating a request is harmless.
            if (change.Conversation.Name != name)
            {
                change.Conversation.Name = name;
                row = _outbox.Create(new ConversationRenamed(request.ConversationId));
                await change.CommitAsync([], [row], cancellationToken);
            }

            update = new ConversationUpdateDto(
                change.Conversation.Id, name, AvatarKeys.PublicUrl(change.Conversation.AvatarKey));
        }

        if (row is not null)
        {
            _logger.LogInformation(
                "User {UserId} renamed group {ConversationId}", request.ActorId, request.ConversationId);

            await _outbox.DeliverAsync(
                row, () => _notifier.NotifyUpdatedAsync(request.ConversationId, cancellationToken), cancellationToken);
        }

        return update;
    }
}
