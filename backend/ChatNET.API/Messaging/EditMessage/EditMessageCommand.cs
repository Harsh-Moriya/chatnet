using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.EditMessage;

// UserId comes from the token, never from the request body. Ciphertext is the new text sealed in the sender's browser, and
// EditCount the number of the edit it was sealed for (the count it carries is part of what was signed), which must follow the one
// stored; both are nullable because a client can leave them out of the JSON, which the validator rejects. MentionedUserIds are the
// people the new text names.
public record EditMessageCommand(
    Guid ConversationId,
    Guid MessageId,
    Guid UserId,
    string? Ciphertext,
    int? EditCount,
    IReadOnlyList<Guid>? MentionedUserIds
) : IRequest<MessageDto>;
