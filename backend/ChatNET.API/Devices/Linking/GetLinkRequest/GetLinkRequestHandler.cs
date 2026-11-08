using ChatNET.API.Common.Exceptions;
using ChatNET.API.Devices.Models;
using ChatNET.API.Devices.Persistence;
using ChatNET.API.Keys;
using MediatR;

namespace ChatNET.API.Devices.Linking.GetLinkRequest;

public class GetLinkRequestHandler : IRequestHandler<GetLinkRequestQuery, LinkRequestStatusDto>
{
    private readonly ILinkRequestRepository _requests;
    private readonly TimeProvider _time;
    private readonly ILogger<GetLinkRequestHandler> _logger;

    public GetLinkRequestHandler(ILinkRequestRepository requests, TimeProvider time, ILogger<GetLinkRequestHandler> logger)
    {
        _requests = requests;
        _time = time;
        _logger = logger;
    }

    public async Task<LinkRequestStatusDto> Handle(GetLinkRequestQuery request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var link = await _requests.FindAsync(request.UserId, request.RequestId, cancellationToken)
                   ?? throw new NotFoundException("Link request", request.RequestId);

        var expiresAt = link.ExpiresAt;

        if (link.ApprovedAt is null)
            return new LinkRequestStatusDto(expiresAt > now ? LinkRequestState.Pending : LinkRequestState.Expired, null, expiresAt);

        if (link.CollectedAt is not null)
            return new LinkRequestStatusDto(LinkRequestState.Collected, null, expiresAt);

        // The collection is the step that hands the bytes over and clears them, so asking twice together gives them to one. The loser,
        // or a caller after the expiry, is told what the state is now.
        var payload = await _requests.TryCollectAsync(request.UserId, request.RequestId, now, cancellationToken);
        if (payload is null)
        {
            var current = await _requests.FindAsync(request.UserId, request.RequestId, cancellationToken);
            var state = current?.CollectedAt is not null ? LinkRequestState.Collected : LinkRequestState.Expired;
            return new LinkRequestStatusDto(state, null, expiresAt);
        }

        _logger.LogInformation("User {UserId} collected the secrets of link request {RequestId}", request.UserId, request.RequestId);

        return new LinkRequestStatusDto(LinkRequestState.Approved, KeyFormats.Encode(payload), expiresAt);
    }
}
