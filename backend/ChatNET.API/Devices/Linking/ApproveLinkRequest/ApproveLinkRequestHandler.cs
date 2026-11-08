using ChatNET.API.Common.Exceptions;
using ChatNET.API.Devices.Persistence;
using ChatNET.API.Keys;
using MediatR;

namespace ChatNET.API.Devices.Linking.ApproveLinkRequest;

public class ApproveLinkRequestHandler : IRequestHandler<ApproveLinkRequestCommand, Unit>
{
    private readonly ILinkRequestRepository _requests;
    private readonly TimeProvider _time;
    private readonly ILogger<ApproveLinkRequestHandler> _logger;

    public ApproveLinkRequestHandler(ILinkRequestRepository requests, TimeProvider time, ILogger<ApproveLinkRequestHandler> logger)
    {
        _requests = requests;
        _time = time;
        _logger = logger;
    }

    public async Task<Unit> Handle(ApproveLinkRequestCommand request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var payload = KeyFormats.Decode(request.Payload, ApproveLinkRequestCommandValidator.MaxPayloadBytes)!;

        if (await _requests.TryApproveAsync(request.UserId, request.RequestId, payload, now, cancellationToken))
        {
            _logger.LogInformation("User {UserId} approved link request {RequestId}", request.UserId, request.RequestId);
            return Unit.Value;
        }

        // Told apart: a request that is not the caller's or does not exist is a 404, one that exists but was approved already, or
        // expired, is a conflict the approving device can show.
        if (await _requests.FindAsync(request.UserId, request.RequestId, cancellationToken) is null)
            throw new NotFoundException("Link request", request.RequestId);

        throw new ConflictException("This request to link a device is no longer open.");
    }
}
