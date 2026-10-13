using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageHistory;

// Neither cursor means the latest page. Before is the id of the oldest message the caller already
// has (read upwards); After is the id of the newest one (read what came since). Not both.
public record GetMessageHistoryQuery(Guid ConversationId, Guid UserId, Guid? Before = null, Guid? After = null)
    : IRequest<List<MessageDto>>;
