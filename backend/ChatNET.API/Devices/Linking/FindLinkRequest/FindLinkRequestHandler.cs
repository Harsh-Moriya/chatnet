using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Devices.Models;
using ChatNET.API.Devices.Persistence;
using ChatNET.API.Keys;
using MediatR;

namespace ChatNET.API.Devices.Linking.FindLinkRequest;

public class FindLinkRequestHandler : IRequestHandler<FindLinkRequestQuery, LinkRequestDto>
{
    private readonly ILinkRequestRepository _requests;
    private readonly TimeProvider _time;

    public FindLinkRequestHandler(ILinkRequestRepository requests, TimeProvider time)
    {
        _requests = requests;
        _time = time;
    }

    // Only the caller's own requests are searched, so a code reveals nothing to anyone but the person it belongs to. One 404
    // for every reason there is nothing to show: no such code, expired, or already approved.
    public async Task<LinkRequestDto> Handle(FindLinkRequestQuery request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var link = await _requests.FindOpenByCodeAsync(request.UserId, request.Code!, now, cancellationToken)
                   ?? throw new NotFoundException("No open request to link a device has this code.");

        return new LinkRequestDto(
            link.Id, KeyFormats.Encode(link.TemporaryPublicKey), link.Kind, link.Name,
            link.CreatedAt.TruncateToMicroseconds(), link.ExpiresAt.TruncateToSeconds());
    }
}
