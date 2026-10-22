namespace ChatNET.API.Messaging.Models;

// A file attached to a message, as a client receives it. Url is a link to the file that the server signed
// for this response; it stops working after Storage:DownloadUrlMinutes, and the client asks
// GET /api/attachments/{id}/url for a new one. An image or a video is meant to be shown in the page; any
// other file is served as a download under its own name.
public record AttachmentDto(
    Guid Id,
    AttachmentType Type,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url
);
