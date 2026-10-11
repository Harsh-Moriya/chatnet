using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.SendMessage;

// SenderId comes from the token, never from the request body. Content is nullable only because
// a client can leave it out of the JSON; the validator rejects that.
public record SendMessageCommand(
    Guid ConversationId,
    Guid SenderId,
    string? Content,
    Guid? ClientMessageId
) : IRequest<MessageDto>;
