using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageFiles;

// The messages of a conversation that have files, newest first. No cursor means the newest page; Before is the id of the oldest message
// the caller already has from this list, and the page just older than it comes back.
public record GetMessageFilesQuery(Guid ConversationId, Guid UserId, Guid? Before = null)
    : IRequest<List<MessageDto>>;
