using ChatNET.API.Media;
using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.SendMessage;

// SenderId comes from the token, never from the request body. Content is nullable because a client can leave
// it out of the JSON, which the validator rejects unless the message carries files. Attachments name files that
// were already uploaded (see CreateUpload).
public record SendMessageCommand(
    Guid ConversationId,
    Guid SenderId,
    string? Content,
    Guid? ClientMessageId,
    IReadOnlyList<AttachmentInput>? Attachments = null
) : IRequest<SendMessageResult>;

// The message, and whether this request created it. False means the same send had already been stored
// (a retry), and the message returned is the one stored then.
public record SendMessageResult(MessageDto Message, bool Created);
