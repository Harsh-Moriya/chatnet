using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Keys.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;

namespace ChatNET.API.Conversations.CreateKeyEpoch;

public class CreateKeyEpochHandler : IRequestHandler<CreateKeyEpochCommand, Unit>
{
    private readonly IConversationRepository _conversations;
    private readonly IKeyEpochRepository _epochs;
    private readonly IKeyRepository _keys;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger<CreateKeyEpochHandler> _logger;

    public CreateKeyEpochHandler(
        IConversationRepository conversations,
        IKeyEpochRepository epochs,
        IKeyRepository keys,
        ConversationNotifier notifier,
        OutboxPublisher outbox,
        TimeProvider time,
        ILogger<CreateKeyEpochHandler> logger)
    {
        _conversations = conversations;
        _epochs = epochs;
        _keys = keys;
        _notifier = notifier;
        _outbox = outbox;
        _time = time;
        _logger = logger;
    }

    // Any member may store the next epoch, whatever their role: a key must be replaceable by whoever is there when it needs to
    // be (the owner may be away), and a member who finds the current one unusable can supersede it. What keeps it safe is not who
    // asks but that the epoch must be made for exactly the people in the conversation, checked while holding the same lock as every
    // change of membership. How often is limited too: a conversation's keys are not replaced again within a day unless a removal or a
    // leave has marked them as due.
    public async Task<Unit> Handle(CreateKeyEpochCommand request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();
        var submission = EpochSubmission.From(request.Epoch);

        OutboxMessage row;
        await using (var change = await _conversations.BeginMembershipChangeAsync(request.ConversationId, cancellationToken)
                                  ?? throw new ForbiddenException(ConversationErrors.NotAMember))
        {
            change.Members.RequireMember(request.UserId);

            // Held until the commit: a new generation of a member's key cannot be stored between reading it and saving the epoch.
            var generations = await _keys.HoldCurrentGenerationsAsync(change.Members.ActiveIds, cancellationToken);
            var newestEpochAt = await _epochs.GetCreatedAtAsync(request.ConversationId, change.Conversation.KeyEpoch, cancellationToken);
            change.StageReplacement(request.UserId, submission, generations, newestEpochAt, now);

            row = _outbox.Create(new ConversationKeyChanged(request.ConversationId));
            await change.CommitAsync([], [row], cancellationToken);
        }

        _logger.LogInformation(
            "User {UserId} stored epoch {Epoch} of conversation {ConversationId} for {MemberCount} members",
            request.UserId, submission.Epoch, request.ConversationId, submission.Envelopes.Count);

        await _outbox.DeliverAsync(
            row, () => _notifier.NotifyKeyChangedAsync(request.ConversationId, cancellationToken), cancellationToken);

        return Unit.Value;
    }
}
