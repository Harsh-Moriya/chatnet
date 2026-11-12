namespace ChatNET.API.Messaging.Models;

// Ciphertext is the text of the message sealed by its sender, as unpadded base64url (see the encryption formats): the server
// cannot read it, and each member's browser opens it with the key of KeyEpoch. It is null for a deleted message. Attachments is
// empty for a message without files and for a deleted one.
//
// ClientMessageId is the UUID the sending client generated for the message. It is part of what was signed, and it is returned
// everywhere the message appears, so the client can swap its temporary message for the confirmed one instead of showing a duplicate.
//
// EditCount and IsDeleted order two copies of one message: the one that was edited more often is the newer, and a
// deleted copy is newer than any other. EditedAt is null for a message nobody edited.
public record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    string? Ciphertext,
    int KeyEpoch,
    DateTime CreatedAt,
    Guid ClientMessageId,
    List<AttachmentDto> Attachments,
    DateTime? EditedAt,
    int EditCount,
    bool IsDeleted
);
