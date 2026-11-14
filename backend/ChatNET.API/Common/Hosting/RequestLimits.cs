namespace ChatNET.API.Common.Hosting;

// What the host accepts in one request, in one place, so a setting that decides how big a request may be is checked against it.
public static class RequestLimits
{
    // The largest request body. Kestrel allows 30 MB by default; the largest legitimate request here is a sealed message of 32 KiB, an
    // encrypted key backup or the keys of an epoch for a group of 100, so a larger body is refused before it is read. Files never pass
    // through the API: the browser sends them straight to the bucket.
    public const long MaxBodyBytes = 1_048_576;

    // What a request carries besides a key backup: a bundle, a password, the field names and the JSON around them.
    private const int BodyBesidesBackupBytes = 4096;

    // The largest key backup that can still be sent. It travels as base64url (four characters for three bytes) inside JSON.
    public const int LargestBackupBytes = (int)((MaxBodyBytes - BodyBesidesBackupBytes) / 4 * 3);
}
