using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.DeleteMessage;

// UserId comes from the token, never from the request body.
public record DeleteMessageCommand(Guid ConversationId, Guid MessageId, Guid UserId) : IRequest<MessageDto>;
