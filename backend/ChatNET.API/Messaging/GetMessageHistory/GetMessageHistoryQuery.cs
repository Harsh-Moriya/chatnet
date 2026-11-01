using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageHistory;

// No cursor means the latest page. Before is the id of the oldest message the caller already has (read upwards); After is the
// id of the newest one (read what came since); Around is the id of a message to be shown in its place, with a page on each
// side (a search result opened in its conversation). Only one of them.
public record GetMessageHistoryQuery(
    Guid ConversationId, Guid UserId, Guid? Before = null, Guid? After = null, Guid? Around = null)
    : IRequest<List<MessageDto>>;
