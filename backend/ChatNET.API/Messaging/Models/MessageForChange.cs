namespace ChatNET.API.Messaging.Models;

// What deciding whether someone may edit or delete a message needs to know about it, without the rest of the row.
public sealed record MessageForChange(
    Guid Id, Guid SenderId, DateTime CreatedAt, bool IsDeleted, string? Content, int AttachmentCount);
