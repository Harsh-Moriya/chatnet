using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Messaging.Models;

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }

    // The text of the message sealed in the browser of its sender: one opaque blob (see MessageBlob) that the server stores and
    // hands on and cannot read. Null exactly when the message is deleted, which removes what was said and keeps the row so the
    // timeline stays intact (no gaps).
    public byte[]? Ciphertext { get; set; }

    // The key period the blob was sealed under, read from its header. It says which key opens the message, and is kept when the
    // message is deleted.
    public int KeyEpoch { get; set; }

    public DateTime CreatedAt { get; set; }

    // The UUID the sending client made up for the message. It is part of what the sender signed, so a message cannot be shown
    // under another id, and it is what a send that arrives twice (a retry after a timeout, a double click) is recognised by:
    // the second finds the first instead of creating another. Unique per conversation and sender, see MessageConfiguration.
    public Guid ClientMessageId { get; set; }

    // The people the latest version of the message names, as the sender said (the server cannot read the text): only members of
    // the conversation, never the sender. An edit notifies the people it names who this did not.
    public Guid[] MentionedUserIds { get; set; } = [];

    public int EditCount { get; set; }
    public DateTime? EditedAt { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // May differ from SenderId when an admin deletes another user's message.
    public Guid? DeletedById { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public AppUser Sender { get; set; } = null!;
    public AppUser? DeletedBy { get; set; }
    public ICollection<MessageAttachment> Attachments { get; set; } = [];
}
