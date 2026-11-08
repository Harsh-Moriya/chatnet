using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.Persistence;
using MediatR;

namespace ChatNET.API.Keys.LookupKeys;

public class LookupKeysHandler : IRequestHandler<LookupKeysQuery, LookedUpKeysDto>
{
    private readonly IKeyRepository _keys;

    public LookupKeysHandler(IKeyRepository keys)
    {
        _keys = keys;
    }

    // Public keys are public: any signed-in person may ask for the current keys of anyone, as they may search for anyone by
    // name. Someone who has not set up encryption is left out of the answer, which is how the caller learns they cannot be
    // sealed a key yet. A person's older generations are not offered, because nothing new is ever sealed to them.
    public async Task<LookedUpKeysDto> Handle(LookupKeysQuery request, CancellationToken cancellationToken)
    {
        var bundles = await _keys.GetCurrentBundlesAsync(request.UserIds.Distinct().ToList(), cancellationToken);

        return new LookedUpKeysDto(
            bundles.Select(b => new UserBundleDto(b.UserId, b.Generation, KeyFormats.Encode(b.Bundle))).ToList());
    }
}
