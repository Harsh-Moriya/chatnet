using MediatR;

namespace ChatNET.API.Notifications.MarkNotificationsRead;

// Ids names the notifications to mark read; null means all of the caller's. Declared as IRequest<Unit> so the
// validator runs (see MarkReadCommand).
public record MarkNotificationsReadCommand(Guid UserId, IReadOnlyList<Guid>? Ids) : IRequest<Unit>;
