using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ChatNET.API.Media;

public sealed class S3BlobStorage : IBlobStorage
{
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _options;
    private readonly TimeProvider _time;

    // A presigned URL uses https unless told otherwise, which would send the browser to an https address on
    // a server that only speaks http (the local one). The scheme of the configured endpoint is used.
    private readonly Protocol _protocol;

    public S3BlobStorage(IAmazonS3 s3, IOptions<StorageOptions> options, TimeProvider time)
    {
        _s3 = s3;
        _options = options.Value;
        _time = time;
        _protocol = new Uri(_options.ServiceUrl).Scheme == Uri.UriSchemeHttps ? Protocol.HTTPS : Protocol.HTTP;
    }

    public SignedUrl CreateUploadUrl(string key, string contentType)
    {
        var expiresAt = _time.GetUtcNow().AddMinutes(_options.UploadUrlMinutes);

        var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
            Protocol = _protocol,
        });

        return new SignedUrl(url, expiresAt);
    }

    public SignedUrl CreateDownloadUrl(string key, string fileName, bool inline)
    {
        var expiresAt = _time.GetUtcNow().AddMinutes(_options.DownloadUrlMinutes);

        // The header builder writes a non-ASCII or quoted name in the encoded form browsers understand, so a
        // file name cannot break out of the header.
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(fileName);

        var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
            Protocol = _protocol,
            ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentDisposition = disposition.ToString() },
        });

        return new SignedUrl(url, expiresAt);
    }

    public async Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var head = await _s3.GetObjectMetadataAsync(_options.Bucket, key, cancellationToken);
            return new StoredObject(head.ContentLength, head.Headers.ContentType, head.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CopyAsync(
        string sourceKey, string destinationKey, string sourceETag, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _options.Bucket,
                SourceKey = sourceKey,
                DestinationBucket = _options.Bucket,
                DestinationKey = destinationKey,
                ETagToMatch = sourceETag,
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new BlobUnavailableException("The object to copy is missing or has changed.", ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        => await _s3.DeleteObjectAsync(_options.Bucket, key, cancellationToken);
}
