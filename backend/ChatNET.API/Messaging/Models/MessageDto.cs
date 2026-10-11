namespace ChatNET.API.Messaging.Models;

// ClientMessageId is the UUID the sending client generated for its optimistic copy.
// It is echoed back (never stored) so the client can swap its temporary message for
// the confirmed one instead of showing a duplicate. It is null on history loads.
public record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    string? Content,
    DateTime CreatedAt,
    Guid? ClientMessageId
);
