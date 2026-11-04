using MediatR;

namespace ChatNET.API.Push.GetPushKey;

public record GetPushKeyQuery : IRequest<PushKeyDto>;

// The key a browser subscribes with (base64url, as the push API of the browser takes it).
public record PushKeyDto(string PublicKey);
