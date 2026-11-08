using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.Persistence;
using MediatR;

namespace ChatNET.API.Keys.GetKeyBackup;

public class GetKeyBackupHandler : IRequestHandler<GetKeyBackupQuery, KeyBackupDto>
{
    private readonly IKeyRepository _keys;
    private readonly ILogger<GetKeyBackupHandler> _logger;

    public GetKeyBackupHandler(IKeyRepository keys, ILogger<GetKeyBackupHandler> logger)
    {
        _keys = keys;
        _logger = logger;
    }

    // Returned to its owner only. Whoever holds the blob can try passphrases against it offline, so the id comes from the
    // token and there is no way to ask for another person's.
    public async Task<KeyBackupDto> Handle(GetKeyBackupQuery request, CancellationToken cancellationToken)
    {
        var backup = await _keys.GetBackupAsync(request.UserId, cancellationToken)
                     ?? throw new NotFoundException("This account has no encryption backup.");

        _logger.LogInformation("User {UserId} fetched their encryption backup", request.UserId);

        return new KeyBackupDto(KeyFormats.Encode(backup.Blob), backup.Version, backup.UpdatedAt.TruncateToMicroseconds());
    }
}
