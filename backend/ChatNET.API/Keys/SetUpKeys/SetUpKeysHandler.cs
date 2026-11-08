using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.Persistence;
using MediatR;

namespace ChatNET.API.Keys.SetUpKeys;

public class SetUpKeysHandler : IRequestHandler<SetUpKeysCommand, KeyStatusDto>
{
    private readonly IKeyRepository _keys;
    private readonly TimeProvider _time;
    private readonly ILogger<SetUpKeysHandler> _logger;

    public SetUpKeysHandler(IKeyRepository keys, TimeProvider time, ILogger<SetUpKeysHandler> logger)
    {
        _keys = keys;
        _time = time;
        _logger = logger;
    }

    public async Task<KeyStatusDto> Handle(SetUpKeysCommand request, CancellationToken cancellationToken)
    {
        // The validator decoded both once; decoding again is cheaper than carrying the bytes in the command.
        var bundle = KeyFormats.Decode(request.Bundle, KeyFormats.BundleBytes)!;
        var backup = KeyFormats.Decode(request.Backup, int.MaxValue)!;
        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        // A second set-up would replace keys other devices and other people already hold, so it is refused. Starting over is a
        // different, deliberate action.
        if (!await _keys.TryCreateAsync(request.UserId, bundle, backup, now, cancellationToken))
            throw new ConflictException("Encryption is already set up for this account.");

        // Ids and sizes only: never a key, a bundle or a backup.
        _logger.LogInformation("User {UserId} set up encryption keys; backup of {BackupBytes} bytes stored", request.UserId, backup.Length);

        return new KeyStatusDto([new BundleDto(1, KeyFormats.Encode(bundle))], new BackupInfoDto(1, now));
    }
}
