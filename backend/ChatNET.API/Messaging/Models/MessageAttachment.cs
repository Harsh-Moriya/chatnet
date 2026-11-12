namespace ChatNET.API.Messaging.Models;

public class MessageAttachment
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }

    // Where the file is in the bucket. A key and not a link, because a link is signed for each reader and
    // stops working after a while (see AttachmentLoader).
    public string StorageKey { get; set; } = string.Empty;

    // The size of the object in the bucket, which is the encrypted file. The name, type and plain size of the file, and the key that
    // opens it, are inside the sealed message and nowhere here.
    public long FileSizeBytes { get; set; }

    // Files keep the order they were attached in, and the table has no column for a position. Each file of a
    // message is stamped one microsecond after the one before it, and reads sort by this. The sealed message lists its files in
    // the same order, which is how a reader knows which is which.
    public DateTime CreatedAt { get; set; }

    public Message Message { get; set; } = null!;
}
