using MediatR;

namespace ChatNET.API.Push.SavePushSubscription;

// What the push API of a browser gives a page (PushSubscription.toJSON): the endpoint and the two keys the payload is
// encrypted to. Declared as IRequest<Unit> so the validator runs (see MarkReadCommand).
public record SavePushSubscriptionCommand(Guid UserId, string? Endpoint, string? P256dh, string? Auth) : IRequest<Unit>;
