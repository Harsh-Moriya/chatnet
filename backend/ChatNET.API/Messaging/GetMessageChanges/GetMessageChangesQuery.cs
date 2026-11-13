using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageChanges;

// Since is the number of the last change the caller has seen, or 0 to read from the start. It is nullable only because a client can
// leave it out of the query string; the validator rejects that.
public record GetMessageChangesQuery(Guid ConversationId, Guid UserId, long? Since) : IRequest<MessageChangesDto>;
