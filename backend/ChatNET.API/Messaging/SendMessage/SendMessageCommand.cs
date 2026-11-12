using ChatNET.API.Media;
using ChatNET.API.Messaging.Models;
using MediatR;

namespace ChatNET.API.Messaging.SendMessage;

// SenderId comes from the token, never from the request body. Ciphertext is the message sealed in the sender's browser, as
// unpadded base64url; it and ClientMessageId are nullable because a client can leave them out of the JSON, which the validator
// rejects. MentionedUserIds are the people the text names, said next to it because the server cannot read the text. Attachments
// name files that were already uploaded (see CreateUpload).
public record SendMessageCommand(
    Guid ConversationId,
    Guid SenderId,
    string? Ciphertext,
    Guid? ClientMessageId,
    IReadOnlyList<Guid>? MentionedUserIds = null,
    IReadOnlyList<AttachmentInput>? Attachments = null
) : IRequest<SendMessageResult>;

// The message, and whether this request created it. False means the same send had already been stored
// (a retry), and the message returned is the one stored then.
public record SendMessageResult(MessageDto Message, bool Created);
