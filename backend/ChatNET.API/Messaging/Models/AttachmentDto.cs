namespace ChatNET.API.Messaging.Models;

// A file attached to a message, as a client receives it. Url is a link to the encrypted file that the server signed for this
// response; it stops working after Storage:DownloadUrlMinutes, and the client asks GET /api/attachments/{id}/url for a new one.
// SizeBytes is the size of the encrypted file. What the file is called and what it is are inside the sealed message, in the same
// position as this attachment has among the message's attachments.
public record AttachmentDto(
    Guid Id,
    long SizeBytes,
    string Url
);
