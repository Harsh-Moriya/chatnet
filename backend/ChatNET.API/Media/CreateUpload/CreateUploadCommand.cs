using MediatR;

namespace ChatNET.API.Media.CreateUpload;

// UserId comes from the token, never from the request body. The name and type are nullable only because a
// client can leave them out of the JSON; the validator rejects that.
public record CreateUploadCommand(
    Guid ConversationId,
    Guid UserId,
    string? FileName,
    string? ContentType,
    long SizeBytes
) : IRequest<UploadTicketDto>;

// Permission to upload one file. UploadKey is what the message names later to attach it, and Url is where the
// browser sends the file (a PUT with exactly this content type) before ExpiresAt.
public record UploadTicketDto(string UploadKey, string Url, DateTimeOffset ExpiresAt);
