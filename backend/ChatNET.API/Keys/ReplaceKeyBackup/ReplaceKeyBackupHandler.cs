using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.Persistence;
using MediatR;

namespace ChatNET.API.Keys.ReplaceKeyBackup;

public class ReplaceKeyBackupHandler : IRequestHandler<ReplaceKeyBackupCommand, BackupInfoDto>
{
    private readonly IKeyRepository _keys;
    private readonly TimeProvider _time;
    private readonly ILogger<ReplaceKeyBackupHandler> _logger;

    public ReplaceKeyBackupHandler(IKeyRepository keys, TimeProvider time, ILogger<ReplaceKeyBackupHandler> logger)
    {
        _keys = keys;
        _time = time;
        _logger = logger;
    }

    public async Task<BackupInfoDto> Handle(ReplaceKeyBackupCommand request, CancellationToken cancellationToken)
    {
        var blob = KeyFormats.Decode(request.Blob, int.MaxValue)!;
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        var version = await _keys.TryReplaceBackupAsync(request.UserId, blob, request.ExpectedVersion!.Value, now, cancellationToken);

        if (version is null)
        {
            // Told apart so a device knows whether to read the backup again (it changed) or to set up first (there is none).
            if (await _keys.GetBackupAsync(request.UserId, cancellationToken) is null)
                throw new NotFoundException("This account has no encryption backup.");

            throw new ConflictException("The backup was changed from another device. Fetch it again and retry.");
        }

        _logger.LogInformation("User {UserId} replaced their encryption backup; it is now version {Version}", request.UserId, version);

        return new BackupInfoDto(version.Value, now);
    }
}
