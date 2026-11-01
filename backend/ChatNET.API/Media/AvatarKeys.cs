namespace ChatNET.API.Media;

// Whose picture it is.
public enum AvatarOwner
{
    User,
    Group,
}

// Where pictures are kept in the bucket, and the address the browser loads them from. An upload first lands under
// pending/, in a folder made from the owner, so the key itself says whose picture it may become and the server needs no
// record of an upload. When the picture is set it is copied to avatars/ under a new random name, so a changed picture is
// a new address and a copy kept by a browser or a proxy can never show the old one in place of the new.
public static class AvatarKeys
{
    public static string NewUploadKey(AvatarOwner owner, Guid ownerId)
        => $"pending/avatars/{Folder(owner)}/{ownerId}/{Guid.NewGuid()}";

    public static string StoredKey(AvatarOwner owner, Guid ownerId, Guid fileId)
        => $"avatars/{Folder(owner)}/{ownerId}/{fileId}";

    // True only for a key made by NewUploadKey for exactly this owner. Compared as text, in the form NewUploadKey writes,
    // so a key that differs only in case or form is not accepted.
    public static bool IsUploadKeyFor(string key, AvatarOwner owner, Guid ownerId)
    {
        var prefix = $"pending/avatars/{Folder(owner)}/{ownerId}/";

        return key.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key[prefix.Length..], "D", out var upload)
            && key == prefix + upload;
    }

    // The address of a stored picture: the key under /api, which is where AvatarEndpoints serves it. Null when there is no
    // picture.
    public static string? PublicUrl(string? storedKey)
        => storedKey is null ? null : "/api/" + storedKey;

    private static string Folder(AvatarOwner owner) => owner == AvatarOwner.User ? "users" : "groups";
}
