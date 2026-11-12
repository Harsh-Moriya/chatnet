namespace ChatNET.API.Media;

// What the server knows about a file: nothing but its size. The browser encrypts every file in fixed-size chunks under a key that
// only travels inside the sealed message, and uploads the result, so the bucket holds bytes nobody here can read, and the server
// can neither check their content type nor see their name. These are the limits it can still keep, on the size of what was stored.
public static class SealedFile
{
    // The one content type an upload is signed for. The bucket never learns what the bytes are.
    public const string ContentType = "application/octet-stream";

    private const int HeaderBytes = 13;
    private const int TagBytes = 16;

    // The chunk size browsers use. A client may choose another (the format allows 1 byte to 16 MiB), which only changes how much
    // overhead its own file carries, so this is the size a file of the limit becomes at the usual one.
    private const int UsualChunkBytes = 64 * 1024;

    // The header and the tag of the one chunk an empty file has: nothing smaller can be a sealed file.
    public const int MinBytes = HeaderBytes + TagBytes;

    // The largest object stored for a file of at most maxFileBytes (the limit as a person sees it, before encryption): the file, the
    // header, and a tag for every chunk.
    public static long MaxBytes(long maxFileBytes)
        => HeaderBytes + maxFileBytes + (maxFileBytes + UsualChunkBytes - 1) / UsualChunkBytes * TagBytes;
}
