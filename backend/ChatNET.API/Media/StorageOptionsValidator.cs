using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Media;

public sealed partial class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    // The bucket naming rules shared by S3 and R2: 3 to 63 lowercase letters, digits and hyphens, starting and
    // ending with a letter or digit.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketName();

    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var serviceUrl)
            || serviceUrl.Scheme is not ("http" or "https"))
            failures.Add("Storage:ServiceUrl must be an absolute http or https address.");

        if (string.IsNullOrWhiteSpace(options.Region))
            failures.Add("Storage:Region must be set (R2 uses \"auto\").");

        if (!BucketName().IsMatch(options.Bucket))
            failures.Add("Storage:Bucket must be 3 to 63 lowercase letters, digits or hyphens.");

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            failures.Add("Storage:AccessKey and Storage:SecretKey must be set (use dotnet user-secrets or environment variables).");

        if (options.MaxFileBytes <= 0)
            failures.Add("Storage:MaxFileBytes must be greater than zero.");

        if (options.MaxFilesPerMessage is < 1 or > 20)
            failures.Add("Storage:MaxFilesPerMessage must be between 1 and 20.");

        if (options.AllowedContentTypes.Length == 0
            || options.AllowedContentTypes.Any(type => string.IsNullOrWhiteSpace(type) || !type.Contains('/')))
            failures.Add("Storage:AllowedContentTypes must list at least one type, each written as type/subtype.");

        if (options.UploadUrlMinutes <= 0)
            failures.Add("Storage:UploadUrlMinutes must be greater than zero.");

        if (options.DownloadUrlMinutes <= 0)
            failures.Add("Storage:DownloadUrlMinutes must be greater than zero.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
