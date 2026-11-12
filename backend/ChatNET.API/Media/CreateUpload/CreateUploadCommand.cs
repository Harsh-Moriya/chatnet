using MediatR;

namespace ChatNET.API.Media.CreateUpload;

// UserId comes from the token, never from the request body. SizeBytes is the size of the encrypted file the browser is about to
// upload: the server is told nothing else about it, neither its name nor its type.
public record CreateUploadCommand(
    Guid ConversationId,
    Guid UserId,
    long SizeBytes
) : IRequest<UploadTicketDto>;

// Permission to upload one file. UploadKey is what the message names later to attach it, and Url is where the
// browser sends the file (a PUT with the content type application/octet-stream) before ExpiresAt.
public record UploadTicketDto(string UploadKey, string Url, DateTime ExpiresAt);
