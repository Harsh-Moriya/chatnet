using ChatNET.API.Common.Time;
using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.Persistence;
using MediatR;

namespace ChatNET.API.Keys.GetKeyStatus;

public class GetKeyStatusHandler : IRequestHandler<GetKeyStatusQuery, KeyStatusDto>
{
    private readonly IKeyRepository _keys;

    public GetKeyStatusHandler(IKeyRepository keys)
    {
        _keys = keys;
    }

    // Only the caller's own state: the id comes from the token. Other people's public keys are a different endpoint (the lookup),
    // open to any signed-in person because a conversation starts with the other person's keys, before anything is shared.
    public async Task<KeyStatusDto> Handle(GetKeyStatusQuery request, CancellationToken cancellationToken)
    {
        var bundles = await _keys.GetBundlesAsync(request.UserId, cancellationToken);
        var backup = await _keys.GetBackupAsync(request.UserId, cancellationToken);

        return new KeyStatusDto(
            bundles.Select(b => new BundleDto(b.Generation, KeyFormats.Encode(b.Bundle))).ToList(),
            backup is null ? null : new BackupInfoDto(backup.Version, backup.UpdatedAt.TruncateToMicroseconds()));
    }
}
