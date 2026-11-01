using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Users;
using ChatNET.API.Users.Persistence;
using MediatR;

namespace ChatNET.API.Media.Avatars;

// Setting or removing a picture, for a person or a group. One change replaces one stored picture with another (or none), in
// one transaction behind the lock of its owner, together with the outbox rows that tell people and remove the old object. The
// object is copied to its permanent place before the lock is taken, so the lock is not held over a call to the storage, and
// the copy is removed again if the change does not happen.
public class ChangeAvatarHandler :
    IRequestHandler<SetAvatarCommand, AvatarDto>,
    IRequestHandler<RemoveAvatarCommand, AvatarDto>
{
    private readonly IProfileRepository _profiles;
    private readonly IConversationRepository _conversations;
    private readonly AvatarIntake _intake;
    private readonly AvatarRemover _remover;
    private readonly ProfileNotifier _profileNotifier;
    private readonly ConversationNotifier _conversationNotifier;
    private readonly OutboxPublisher _outbox;
    private readonly ILogger<ChangeAvatarHandler> _logger;

    public ChangeAvatarHandler(
        IProfileRepository profiles,
        IConversationRepository conversations,
        AvatarIntake intake,
        AvatarRemover remover,
        ProfileNotifier profileNotifier,
        ConversationNotifier conversationNotifier,
        OutboxPublisher outbox,
        ILogger<ChangeAvatarHandler> logger)
    {
        _profiles = profiles;
        _conversations = conversations;
        _intake = intake;
        _remover = remover;
        _profileNotifier = profileNotifier;
        _conversationNotifier = conversationNotifier;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<AvatarDto> Handle(SetAvatarCommand request, CancellationToken cancellationToken)
    {
        var newKey = await _intake.AdoptAsync(request.Owner, request.OwnerId, request.UploadKey!, cancellationToken);
        return await ChangeAsync(request.Owner, request.OwnerId, request.ActorId, newKey, cancellationToken);
    }

    public Task<AvatarDto> Handle(RemoveAvatarCommand request, CancellationToken cancellationToken)
        => ChangeAsync(request.Owner, request.OwnerId, request.ActorId, null, cancellationToken);

    private async Task<AvatarDto> ChangeAsync(
        AvatarOwner owner, Guid ownerId, Guid actorId, string? newKey, CancellationToken cancellationToken)
    {
        string? oldKey;
        OutboxMessage? announce = null;
        OutboxMessage? cleanup = null;

        try
        {
            if (owner == AvatarOwner.User)
            {
                await using var change = await _profiles.BeginProfileChangeAsync(ownerId, cancellationToken)
                                         ?? throw new NotFoundException("User", ownerId);

                if (change.User.IsDeactivated)
                    throw new ForbiddenException("This account can no longer be changed.");

                oldKey = change.User.AvatarKey;
                if (oldKey != newKey)
                {
                    change.User.AvatarKey = newKey;
                    announce = _outbox.Create(new ProfileChanged(ownerId));
                    cleanup = oldKey is null ? null : _outbox.Create(new AvatarFileRemoved(oldKey));
                    await change.CommitAsync(Rows(announce, cleanup), cancellationToken);
                }
            }
            else
            {
                await using var change = await _conversations.BeginMembershipChangeAsync(ownerId, cancellationToken)
                                         ?? throw new ForbiddenException(ConversationErrors.NotAMember);

                change.RequireGroupMember(actorId);
                change.Members.RequireManager(actorId, "change this group's picture");

                oldKey = change.Conversation.AvatarKey;
                if (oldKey != newKey)
                {
                    change.Conversation.AvatarKey = newKey;
                    announce = _outbox.Create(new ConversationRenamed(ownerId));
                    cleanup = oldKey is null ? null : _outbox.Create(new AvatarFileRemoved(oldKey));
                    await change.CommitAsync([], Rows(announce, cleanup), cancellationToken);
                }
            }
        }
        catch
        {
            // Nothing was saved, so the picture that was just copied belongs to no one.
            if (newKey is not null)
                await _intake.DiscardAsync(newKey);

            throw;
        }

        if (announce is null)
        {
            // The same picture state again (removing a picture that is not there): stored and said nothing.
            return new AvatarDto(AvatarKeys.PublicUrl(newKey));
        }

        _logger.LogInformation(
            "User {UserId} {Action} the picture of {Owner} {OwnerId}",
            actorId, newKey is null ? "removed" : "set", owner, ownerId);

        await _outbox.DeliverAsync(announce, () => owner == AvatarOwner.User
            ? _profileNotifier.NotifyChangedAsync(ownerId, cancellationToken)
            : _conversationNotifier.NotifyUpdatedAsync(ownerId, cancellationToken), cancellationToken);

        if (cleanup is not null)
            await _outbox.DeliverAsync(cleanup, () => _remover.RemoveAsync(oldKey!, cancellationToken), cancellationToken);

        return new AvatarDto(AvatarKeys.PublicUrl(newKey));
    }

    private static List<OutboxMessage> Rows(OutboxMessage announce, OutboxMessage? cleanup)
        => cleanup is null ? [announce] : [announce, cleanup];
}
