using ChatNET.API.Common.Exceptions;
using ChatNET.API.Media;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Users.Persistence;
using ChatNET.API.Users.SearchUsers;
using MediatR;

namespace ChatNET.API.Users.UpdateProfile;

public class UpdateProfileHandler : IRequestHandler<UpdateProfileCommand, UserSummaryDto>
{
    private readonly IProfileRepository _profiles;
    private readonly ProfileNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly ILogger<UpdateProfileHandler> _logger;

    public UpdateProfileHandler(
        IProfileRepository profiles, ProfileNotifier notifier, OutboxPublisher outbox, ILogger<UpdateProfileHandler> logger)
    {
        _profiles = profiles;
        _notifier = notifier;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<UserSummaryDto> Handle(UpdateProfileCommand request, CancellationToken cancellationToken)
    {
        var displayName = request.DisplayName!.Trim();

        UserSummaryDto profile;
        OutboxMessage? row = null;

        await using (var change = await _profiles.BeginProfileChangeAsync(request.UserId, cancellationToken)
                                  ?? throw new NotFoundException("User", request.UserId))
        {
            if (change.User.IsDeactivated)
                throw new ForbiddenException("This account can no longer be changed.");

            // The same name again stores and says nothing, so repeating a request is harmless.
            if (change.User.DisplayName != displayName)
            {
                change.User.DisplayName = displayName;
                row = _outbox.Create(new ProfileChanged(request.UserId));
                await change.CommitAsync([row], cancellationToken);
            }

            profile = new UserSummaryDto(
                change.User.Id, change.User.UserName!, displayName, AvatarKeys.PublicUrl(change.User.AvatarKey));
        }

        if (row is not null)
        {
            _logger.LogInformation("User {UserId} changed their display name", request.UserId);

            await _outbox.DeliverAsync(
                row, () => _notifier.NotifyChangedAsync(request.UserId, cancellationToken), cancellationToken);
        }

        return profile;
    }
}
