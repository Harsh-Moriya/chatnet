namespace ChatNET.API.Media;

// A file named in a request to send a message: the key the upload ticket gave. It is nullable only because a client can leave it
// out of the JSON; the validator rejects that. The name of the file is inside the sealed message, not here.
public record AttachmentInput(string? UploadKey);
