namespace ChatNET.API.Media;

// Where files are kept in the bucket. An upload first lands under pending/, in a folder made from the
// conversation and the person it was for, so the key itself says who may attach it and the server needs no
// record of an upload. When the message is sent the file is copied to attachments/ under the id of its
// attachment row; what is left under pending/ is removed, or expires by the bucket's lifecycle rule.
public static class AttachmentKeys
{
    public static string NewUploadKey(Guid conversationId, Guid userId)
        => $"pending/{conversationId}/{userId}/{Guid.NewGuid()}";

    public static string StoredKey(Guid conversationId, Guid attachmentId)
        => $"attachments/{conversationId}/{attachmentId}";

    // True only for a key made by NewUploadKey for exactly this conversation and this person. Compared as
    // text, in the form NewUploadKey writes, so a key that differs only in case or form is not accepted.
    public static bool IsUploadKeyFor(string key, Guid conversationId, Guid userId)
    {
        var prefix = $"pending/{conversationId}/{userId}/";

        return key.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key[prefix.Length..], "D", out var upload)
            && key == prefix + upload;
    }
}
