using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Devices.Models;
using ChatNET.API.Devices.Persistence;
using ChatNET.API.Keys;
using ChatNET.API.Keys.Persistence;
using MediatR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Devices.Linking.CreateLinkRequest;

public class CreateLinkRequestHandler : IRequestHandler<CreateLinkRequestCommand, LinkRequestCreatedDto>
{
    private readonly ILinkRequestRepository _requests;
    private readonly IKeyRepository _keys;
    private readonly EncryptionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CreateLinkRequestHandler> _logger;

    public CreateLinkRequestHandler(
        ILinkRequestRepository requests, IKeyRepository keys, IOptions<EncryptionOptions> options, TimeProvider time,
        ILogger<CreateLinkRequestHandler> logger)
    {
        _requests = requests;
        _keys = keys;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<LinkRequestCreatedDto> Handle(CreateLinkRequestCommand request, CancellationToken cancellationToken)
    {
        // There is nothing to link to until the person has keys somewhere.
        if (!await _keys.HasKeysAsync(request.UserId, cancellationToken))
            throw new ConflictException("Set up encryption before linking a device.");

        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();
        var link = new DeviceLinkRequest
        {
            Id = Guid.CreateVersion7(),
            UserId = request.UserId,
            Code = request.Code!,
            TemporaryPublicKey = KeyFormats.Decode(request.TemporaryPublicKey, KeyFormats.KeyBytes)!,
            Kind = DeviceRules.ParseKind(request.Kind!),
            Name = DeviceRules.CleanName(request.Name!),
            CreatedAt = now,
            ExpiresAt = (now + _options.LinkLifetime).TruncateToSeconds(),
        };

        if (!await _requests.TryAddAsync(link, _options.MaxPendingLinkRequests, now, cancellationToken))
            throw new ConflictException("Too many link requests are open. Wait for one to expire, or try again.");

        // Never the code or the key.
        _logger.LogInformation("User {UserId} opened link request {RequestId}", request.UserId, link.Id);

        return new LinkRequestCreatedDto(link.Id, link.ExpiresAt);
    }
}
