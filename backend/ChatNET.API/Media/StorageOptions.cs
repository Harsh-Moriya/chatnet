namespace ChatNET.API.Media;

// Bound from the "Storage" configuration section and validated when the host starts. The access key and
// secret come from user-secrets or the environment, never from a file that is committed.
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    // The S3 endpoint, reachable from the browser as well as from the server: the same address is signed
    // into the upload and download URLs the browser uses. Locally that is RustFS on http://localhost:9000,
    // in production the Cloudflare R2 endpoint of the account.
    public string ServiceUrl { get; set; } = string.Empty;

    // R2 wants "auto"; most S3-compatible servers accept any value.
    public string Region { get; set; } = "auto";

    public string Bucket { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    // The largest file, as a person sees it: before it is encrypted. What is stored for it is a little larger (see SealedFile). The
    // limit is checked when an upload is announced and again when the stored object is looked at, because a presigned PUT cannot
    // cap the size of what is sent. The server cannot see what a file is, so size and count are all it limits.
    public long MaxFileBytes { get; set; }
    public int MaxFilesPerMessage { get; set; }

    // How long a browser has to start an upload after asking for permission to make it.
    public int UploadUrlMinutes { get; set; }

    // How long a download link lasts. A client that finds a link expired asks for a fresh one.
    public int DownloadUrlMinutes { get; set; }
}
