using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Calls.Ice;

// Bound from the "Turn" configuration section and validated when the host starts. A call goes straight between two browsers when it
// can; when neither side can reach the other (each behind a NAT that allows no inbound traffic) it goes through a relay, a TURN
// server, that both sides can reach. The server and the API share a secret: the API makes a credential for one person from it (see
// IceServers) that the server can check without asking anyone, and that stops working after CredentialLifetime. The secret is a
// secret of the deployment and never in the code (set it with user-secrets or the environment). Without it the feature is off: the
// URLs may still be set (the development settings carry them), and nothing is handed out.
public sealed class TurnOptions
{
    public const string SectionName = "Turn";

    // The key the credentials are made with. The relay is started with the same one (TURN_SECRET in the compose files).
    public string Secret { get; set; } = string.Empty;

    // Where the relay is reached, as a browser takes them: "turn:chat.example.com:3478?transport=udp", and the same with tcp.
    public List<string> Urls { get; set; } = [];

    // Where a browser can learn its own public address, "stun:chat.example.com:3478" (the relay answers these too). Optional.
    public List<string> StunUrls { get; set; } = [];

    // How long a credential works for making an allocation, which is what a stolen or leaked one can be used for. An allocation that
    // already exists was seen to keep relaying past its credential's end (a call relayed for 15 minutes on a 75 second credential), so this
    // is not the length of a call.
    public TimeSpan CredentialLifetime { get; set; } = TimeSpan.FromHours(1);

    public bool Enabled => Secret.Length > 0;
}

public sealed partial class TurnOptionsValidator : IValidateOptions<TurnOptions>
{
    // The least a secret may be: a guess at a shorter one is within reach, and the credentials are checkable offline by whoever sees one.
    public const int MinSecretLength = 32;

    public static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    // turn:host[:port][?transport=udp|tcp] and stun:host[:port], as RFC 7064 and 7065 write them. A host is a name, an IPv4 address or an
    // IPv6 address in brackets.
    [GeneratedRegex(@"^turns?:(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(:\d{1,5})?(\?transport=(udp|tcp))?$")]
    private static partial Regex TurnUrl();

    [GeneratedRegex(@"^stuns?:(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(:\d{1,5})?$")]
    private static partial Regex StunUrl();

    public ValidateOptionsResult Validate(string? name, TurnOptions options)
    {
        var failures = new List<string>();

        if (options.CredentialLifetime < MinLifetime || options.CredentialLifetime > MaxLifetime)
            failures.Add("Turn:CredentialLifetime must be between one minute and 24 hours.");

        if (options.Enabled)
        {
            if (options.Secret.Length < MinSecretLength)
                failures.Add($"Turn:Secret must be at least {MinSecretLength} characters.");

            if (options.Urls.Count == 0)
                failures.Add("Turn:Urls must name at least one relay when Turn:Secret is set, such as turn:chat.example.com:3478?transport=udp.");

            foreach (var url in options.Urls.Where(url => !TurnUrl().IsMatch(url)))
                failures.Add($"Turn:Urls has an entry that is not a turn: or turns: address: {url}");

            foreach (var url in options.StunUrls.Where(url => !StunUrl().IsMatch(url)))
                failures.Add($"Turn:StunUrls has an entry that is not a stun: or stuns: address: {url}");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
