namespace ChatNET.API.Messaging.Models;

// ClientMessageId is the UUID the sending client generated for its optimistic copy. It is stored with
// the message and returned everywhere the message appears, so the client can swap its temporary message
// for the confirmed one instead of showing a duplicate. It is null for a message sent without one.
public record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    string? Content,
    DateTime CreatedAt,
    Guid? ClientMessageId
);
