using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations;
using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Keys.ResetKeys;

public class ResetKeysHandler : IRequestHandler<ResetKeysCommand, KeyStatusDto>
{
    private readonly UserManager<AppUser> _userManager;
    private readonly IKeyRepository _keys;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger<ResetKeysHandler> _logger;

    public ResetKeysHandler(
        UserManager<AppUser> userManager, IKeyRepository keys, ConversationNotifier notifier, OutboxPublisher outbox, TimeProvider time,
        ILogger<ResetKeysHandler> logger)
    {
        _userManager = userManager;
        _keys = keys;
        _notifier = notifier;
        _outbox = outbox;
        _time = time;
        _logger = logger;
    }

    public async Task<KeyStatusDto> Handle(ResetKeysCommand request, CancellationToken cancellationToken)
    {
        await ConfirmPasswordAsync(request);

        // The validator decoded both once; decoding again is cheaper than carrying the bytes in the command.
        var bundle = KeyFormats.Decode(request.Bundle, KeyFormats.BundleBytes)!;
        KeyFormats.IsBundle(bundle, out var generation);
        var backup = KeyFormats.Decode(request.Backup, int.MaxValue)!;
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        var result = await _keys.TryResetAsync(
            request.UserId, generation, bundle, backup, conversationId => _outbox.Create(new ConversationKeyChanged(conversationId)), now,
            cancellationToken);

        switch (result.Outcome)
        {
            case GenerationOutcome.NoKeys:
                throw new NotFoundException("This account has no encryption keys.");

            case GenerationOutcome.GenerationStale:
                throw new ConflictException("Another generation of the keys was stored first. Read your keys again.", KeyCodes.GenerationStale);
        }

        // Ids and counts only: never a key, a bundle, a backup or a password.
        _logger.LogInformation(
            "User {UserId} reset their encryption keys to generation {Generation} (backup version {BackupVersion}); every device was removed; {ConversationCount} conversations marked for a new key period",
            request.UserId, generation, result.BackupVersion, result.Conversations.Count);

        foreach (var (conversationId, row) in result.Conversations)
        {
            await _outbox.DeliverAsync(row, () => _notifier.NotifyKeyChangedAsync(conversationId, cancellationToken), cancellationToken);
        }

        var bundles = await _keys.GetBundlesAsync(request.UserId, cancellationToken);
        return new KeyStatusDto(
            bundles.Select(b => new BundleDto(b.Generation, KeyFormats.Encode(b.Bundle))).ToList(),
            new BackupInfoDto(result.BackupVersion, now));
    }

    // The same rules as signing in: a locked account is refused before the password is looked at, and failures count towards the lock.
    // The answer for a wrong password is a 403 and not a 401, so the browser does not take it for an expired session.
    private async Task ConfirmPasswordAsync(ResetKeysCommand request)
    {
        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.IsDeactivated)
            throw new ForbiddenException("This account cannot reset its keys.");

        if (await _userManager.IsLockedOutAsync(user))
        {
            var lockoutEnd = await _userManager.GetLockoutEndDateAsync(user);
            throw new TooManyAttemptsException((lockoutEnd ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow);
        }

        if (!await _userManager.CheckPasswordAsync(user, request.Password!))
        {
            await _userManager.AccessFailedAsync(user);

            if (await _userManager.IsLockedOutAsync(user))
                _logger.LogWarning("Account {UserId} locked out after repeated wrong passwords", user.Id);

            throw new ForbiddenException("The password is not right.", KeyCodes.WrongPassword);
        }

        await _userManager.ResetAccessFailedCountAsync(user);
    }
}
