using ChatNET.API.Common.Hosting;
using ChatNET.API.Email;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.PasswordReset;

// Bound from the "PasswordReset" configuration section and validated when the host starts.
public sealed class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    // How long a link works. Short, because anyone who reads the mailbox in that time can use it; long enough for a mail
    // that is slow to arrive.
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    // How old a request may be and still be sent. The email is sent by the outbox relay, which retries a failure with a growing
    // wait; a link that arrives half an hour after it was asked for is of no use to someone who has given up, and every retry
    // would also make another link. A request older than this is dropped.
    public TimeSpan SendWithin { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed class PasswordResetOptionsValidator : IValidateOptions<PasswordResetOptions>
{
    private readonly EmailOptions _email;
    private readonly FrontendOptions _frontend;

    public PasswordResetOptionsValidator(IOptions<EmailOptions> email, IOptions<FrontendOptions> frontend)
    {
        _email = email.Value;
        _frontend = frontend.Value;
    }

    public ValidateOptionsResult Validate(string? name, PasswordResetOptions options)
    {
        var failures = new List<string>();

        if (options.TokenLifetime < TimeSpan.FromMinutes(1))
            failures.Add("PasswordReset:TokenLifetime must be at least one minute.");

        if (options.SendWithin <= TimeSpan.Zero)
            failures.Add("PasswordReset:SendWithin must be greater than zero.");

        // The link in the email is built from this address, and only when email is on. A link to the wrong place, or one that
        // sends the token over plain http to a real host, is found at the start and not by the first person who asks.
        if (_email.Enabled && !IsWebAppAddress(_frontend.Url))
            failures.Add("Frontend:Url must be the address of the web app when email is on: an absolute https address (http only for localhost) with no path, query or fragment.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsWebAppAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0;
}
