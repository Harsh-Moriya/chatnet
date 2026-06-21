namespace ChatNET.API.Messaging.Models;

public class MessageAttachment
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public string Url { get; set; } = string.Empty;
    public AttachmentType Type { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }

    public Message Message { get; set; } = null!;
}
