using Amazon.S3;
using Amazon.S3.Model;

namespace ChatNET.API.Tests.Infrastructure;

// Direct access to the test bucket, in the same spirit as the database helpers: to arrange objects the API
// will look at, and to see what it left behind.
public static class StorageApi
{
    public static async Task PutObjectAsync(
        this ChatApiFactory factory, string key, byte[] content, string contentType = "image/png")
    {
        using var stream = new MemoryStream(content);
        await factory.Storage.PutObjectAsync(new PutObjectRequest
        {
            BucketName = ChatApiFactory.StorageBucket,
            Key = key,
            ContentType = contentType,
            InputStream = stream,
        });
    }

    public static async Task<bool> ObjectExistsAsync(this ChatApiFactory factory, string key)
    {
        try
        {
            await factory.Storage.GetObjectMetadataAsync(ChatApiFactory.StorageBucket, key);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public static async Task<List<string>> ObjectKeysAsync(this ChatApiFactory factory, string prefix)
    {
        var listed = await factory.Storage.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = ChatApiFactory.StorageBucket,
            Prefix = prefix,
        });

        return listed.S3Objects?.Select(o => o.Key).ToList() ?? [];
    }

    public static async Task<byte[]> GetObjectBytesAsync(this ChatApiFactory factory, string key)
    {
        using var response = await factory.Storage.GetObjectAsync(ChatApiFactory.StorageBucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
