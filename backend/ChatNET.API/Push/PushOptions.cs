using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Push;

// Bound from the "Push" configuration section and validated when the host starts. The three VAPID settings identify this
// server to the browser vendors' push services: a push is signed with the private key, and the browser only accepts pushes
// signed by the server whose public key it subscribed with. They are secrets of the deployment and never in the code (set
// them with user-secrets or the environment). With none of them set the feature is off.
public sealed class PushOptions
{
    public const string SectionName = "Push";

    // The key the browser subscribes with and the key that signs the pushes, as a browser exchanges them (base64url).
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;

    // Who a push service can contact about this sender: "mailto:" an address, or an https address.
    public string Subject { get; set; } = string.Empty;

    // The push services a subscription may name. The server posts to the address a browser registers, so without this list a
    // signed-in person could make it call any address; a host here also matches its subdomains.
    public List<string> AllowedHosts { get; set; } = [];

    // How many browsers one person can have registered. A new one beyond it replaces the one least recently used.
    public int MaxSubscriptionsPerUser { get; set; } = 10;

    // How long a push service keeps a push for a browser that is switched off. An alert that old is of no use.
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(1);

    // How long one request to a push service may take.
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool Enabled => PublicKey.Length > 0 && PrivateKey.Length > 0 && Subject.Length > 0;
}

public sealed class PushOptionsValidator : IValidateOptions<PushOptions>
{
    public ValidateOptionsResult Validate(string? name, PushOptions options)
    {
        var failures = new List<string>();

        var given = new[] { options.PublicKey, options.PrivateKey, options.Subject }.Count(setting => setting.Length > 0);

        // None set is how the feature is turned off. Some set is a mistake that would otherwise show up as the first push
        // failing, long after the start.
        if (given is > 0 and < 3)
            failures.Add("Push:PublicKey, Push:PrivateKey and Push:Subject must all be set, or none of them.");

        if (given == 3)
        {
            Check(failures, "Push:PublicKey", () => VapidKeys.DecodePublicKey(options.PublicKey));
            Check(failures, "Push:PrivateKey", () => VapidKeys.DecodePrivateKey(options.PrivateKey));

            // Only when each is well formed on its own: then they are checked as a pair.
            if (failures.Count == 0)
                Check(failures, "Push:PublicKey", () => VapidKeys.CreateSigner(options.PublicKey, options.PrivateKey).Dispose());

            // RFC 8292 asks for a contact a push service can use.
            if (!options.Subject.StartsWith("mailto:", StringComparison.Ordinal)
                && !options.Subject.StartsWith("https://", StringComparison.Ordinal))
                failures.Add("Push:Subject must be a mailto: or an https: address.");

            if (options.AllowedHosts.Count == 0 || options.AllowedHosts.Any(host => !IsHostName(host)))
                failures.Add("Push:AllowedHosts must name at least one host, each a plain host name such as push.example.com.");
        }

        if (options.MaxSubscriptionsPerUser < 1)
            failures.Add("Push:MaxSubscriptionsPerUser must be at least 1.");

        if (options.TimeToLive < TimeSpan.FromSeconds(1) || options.TimeToLive.TotalSeconds > int.MaxValue)
            failures.Add("Push:TimeToLive must be at least one second.");

        if (options.RequestTimeout <= TimeSpan.Zero)
            failures.Add("Push:RequestTimeout must be greater than zero.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // What is wrong with a key is said without repeating the key, which is a secret (or, for the public half, no use to a reader).
    private static void Check(List<string> failures, string setting, Action validate)
    {
        try
        {
            validate();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or PlatformNotSupportedException)
        {
            failures.Add($"{setting} is not valid: {ex.Message}");
        }
    }

    private static bool IsHostName(string host) =>
        host.Length > 0
        && host == host.Trim().ToLowerInvariant()
        && Uri.CheckHostName(host) == UriHostNameType.Dns;
}
