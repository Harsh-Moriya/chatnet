using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageHistory;

public record GetMessageHistoryQuery(Guid ConversationId, Guid UserId) : IRequest<List<MessageDto>>;
