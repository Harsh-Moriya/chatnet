using ChatNET.API.Common.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Push.GetPushKey;

public class GetPushKeyHandler : IRequestHandler<GetPushKeyQuery, PushKeyDto>
{
    private readonly PushOptions _options;

    public GetPushKeyHandler(IOptions<PushOptions> options)
    {
        _options = options.Value;
    }

    // A server without keys has no alerts to offer, which is how a page knows not to show the switch.
    public Task<PushKeyDto> Handle(GetPushKeyQuery request, CancellationToken cancellationToken) =>
        _options.Enabled
            ? Task.FromResult(new PushKeyDto(_options.PublicKey))
            : throw new NotFoundException(PushMessages.NotSetUp);
}
