using ChatNET.API.Common.Hosting;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Keys;

// Bound from the "Encryption" configuration section and validated when the host starts.
public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    // How long a request to link a new device stays open. The new device shows its code for this long; the code is a short
    // commitment to a key, so the shorter the window the less time anyone has to try to forge one.
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(10);

    // How many devices one person can have. A device is added when it holds the keys of the person, so the limit is the number
    // of places those keys can be.
    public int MaxDevicesPerUser { get; set; } = 10;

    // How many requests to link a device one person can have open at once.
    public int MaxPendingLinkRequests { get; set; } = 5;

    // The largest encrypted key backup accepted, in bytes. A backup holds a few hundred bytes per generation of the account key. It cannot
    // be set above what a request body can carry (RequestLimits.LargestBackupBytes).
    public int MaxBackupBytes { get; set; } = 64 * 1024;
}

public sealed class EncryptionOptionsValidator : IValidateOptions<EncryptionOptions>
{
    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        var failures = new List<string>();

        if (options.LinkLifetime < TimeSpan.FromMinutes(1) || options.LinkLifetime > TimeSpan.FromHours(1))
            failures.Add("Encryption:LinkLifetime must be between 1 minute and 1 hour.");

        if (options.MaxDevicesPerUser is < 1 or > 50)
            failures.Add("Encryption:MaxDevicesPerUser must be between 1 and 50.");

        if (options.MaxPendingLinkRequests is < 1 or > 20)
            failures.Add("Encryption:MaxPendingLinkRequests must be between 1 and 20.");

        if (options.MaxBackupBytes < 1024 || options.MaxBackupBytes > RequestLimits.LargestBackupBytes)
            failures.Add($"Encryption:MaxBackupBytes must be between 1024 and {RequestLimits.LargestBackupBytes}.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
