namespace ChatNET.API.Messaging.Models;

public class MessageAttachment
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }

    // Where the file is in the bucket. A key and not a link, because a link is signed for each reader and
    // stops working after a while (see AttachmentLoader).
    public string StorageKey { get; set; } = string.Empty;

    public AttachmentType Type { get; set; }
    public string FileName { get; set; } = string.Empty;

    // The type the file was stored with. It decides how a browser treats the file, so it is the one the
    // storage reported for the uploaded object and not the one the sender claimed.
    public string ContentType { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    // Files keep the order they were attached in, and the table has no column for a position. Each file of a
    // message is stamped one microsecond after the one before it, and reads sort by this.
    public DateTime CreatedAt { get; set; }

    public Message Message { get; set; } = null!;
}
