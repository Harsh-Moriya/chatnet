using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations;
using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;

namespace ChatNET.API.Keys.AddKeyGeneration;

public class AddKeyGenerationHandler : IRequestHandler<AddKeyGenerationCommand, KeyStatusDto>
{
    private readonly IKeyRepository _keys;
    private readonly ConversationNotifier _notifier;
    private readonly OutboxPublisher _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger<AddKeyGenerationHandler> _logger;

    public AddKeyGenerationHandler(
        IKeyRepository keys, ConversationNotifier notifier, OutboxPublisher outbox, TimeProvider time, ILogger<AddKeyGenerationHandler> logger)
    {
        _keys = keys;
        _notifier = notifier;
        _outbox = outbox;
        _time = time;
        _logger = logger;
    }

    // Everything that makes the new generation count happens in the repository's one transaction: the bundle, the backup that holds
    // its private half, the removal of the lost device and the marking of the person's conversations for a new key period. The
    // server cannot tell whether the bundle was made from the same identity key as before or from a new one: whether the identity
    // changed is for the people who pinned it to see.
    public async Task<KeyStatusDto> Handle(AddKeyGenerationCommand request, CancellationToken cancellationToken)
    {
        // The validator decoded both once; decoding again is cheaper than carrying the bytes in the command.
        var bundle = KeyFormats.Decode(request.Bundle, KeyFormats.BundleBytes)!;
        KeyFormats.IsBundle(bundle, out var generation);
        var backup = KeyFormats.Decode(request.Backup, int.MaxValue)!;
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        var result = await _keys.TryAddGenerationAsync(
            request.UserId, generation, bundle, backup, request.ExpectedBackupVersion!.Value, request.RemoveDeviceId,
            conversationId => _outbox.Create(new ConversationKeyChanged(conversationId)), now, cancellationToken);

        switch (result.Outcome)
        {
            case GenerationOutcome.NoKeys:
                throw new NotFoundException("This account has no encryption keys.");

            case GenerationOutcome.GenerationStale:
                throw new ConflictException("Another generation of the keys was stored first. Read your keys again.", KeyCodes.GenerationStale);

            case GenerationOutcome.BackupVersionStale:
                throw new ConflictException("The backup was changed from another device. Fetch it again and retry.", KeyCodes.BackupVersionStale);

            case GenerationOutcome.DeviceNotFound:
                throw new NotFoundException("Device", request.RemoveDeviceId!.Value);
        }

        // Ids and counts only: never a key, a bundle, a backup or a device name.
        _logger.LogInformation(
            "User {UserId} stored generation {Generation} of their account key (backup version {BackupVersion}); device {DeviceId} removed; {ConversationCount} conversations marked for a new key period",
            request.UserId, generation, result.BackupVersion, request.RemoveDeviceId, result.Conversations.Count);

        foreach (var (conversationId, row) in result.Conversations)
        {
            await _outbox.DeliverAsync(row, () => _notifier.NotifyKeyChangedAsync(conversationId, cancellationToken), cancellationToken);
        }

        var bundles = await _keys.GetBundlesAsync(request.UserId, cancellationToken);
        return new KeyStatusDto(
            bundles.Select(b => new BundleDto(b.Generation, KeyFormats.Encode(b.Bundle))).ToList(),
            new BackupInfoDto(result.BackupVersion, now));
    }
}
