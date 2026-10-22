using Amazon.Runtime;
using Amazon.S3;

namespace ChatNET.API.Media;

public static class S3ClientFactory
{
    public static IAmazonS3 Create(StorageOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            AuthenticationRegion = options.Region,

            // R2 and the local server both answer on one host with the bucket in the path.
            ForcePathStyle = true,

            // Version 4 of the SDK adds a checksum to every upload by default. R2 does not accept it on a
            // presigned URL, and the browser would not send it. Only when an operation requires one.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }
}
