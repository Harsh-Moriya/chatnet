using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.EditMessage;

// UserId comes from the token, never from the request body. Content is nullable because a client can leave it out of
// the JSON, which the validator rejects.
public record EditMessageCommand(
    Guid ConversationId,
    Guid MessageId,
    Guid UserId,
    string? Content
) : IRequest<MessageDto>;
