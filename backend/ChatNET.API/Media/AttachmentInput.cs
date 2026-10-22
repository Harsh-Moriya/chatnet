namespace ChatNET.API.Media;

// A file named in a request to send a message: the key the upload ticket gave, and the name the file had on
// the sender's computer. Both are nullable only because a client can leave them out of the JSON; the validator
// rejects that.
public record AttachmentInput(string? UploadKey, string? FileName);
