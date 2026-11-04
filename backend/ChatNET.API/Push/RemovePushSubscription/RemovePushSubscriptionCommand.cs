using MediatR;

namespace ChatNET.API.Push.RemovePushSubscription;

// Declared as IRequest<Unit> so the validator runs (see MarkReadCommand).
public record RemovePushSubscriptionCommand(Guid UserId, string? Endpoint) : IRequest<Unit>;
