using Microsoft.Extensions.Options;
using MimeKit;

namespace ChatNET.API.Email;

// Bound from the "Email" configuration section and validated when the host starts. The server sends mail through an SMTP
// relay (Mailpit locally, a provider such as Resend in production). With no host set the feature is off: the API still
// starts, and what needs to send mail answers that it is not available.
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public SmtpOptions Smtp { get; set; } = new();

    public SenderOptions From { get; set; } = new();

    public bool Enabled => Smtp.Host.Length > 0;
}

public enum SmtpSecurity
{
    // Plain text first, then upgraded with STARTTLS; a server that does not offer it is refused (port 587 of most relays).
    StartTls,

    // TLS from the first byte (port 465 of most relays).
    SslOnConnect,

    // No encryption. Only for a local mail catcher, so it is refused together with credentials.
    None,
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    // The login of the relay. A secret of the deployment: set it with user-secrets or the environment, never in code.
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    // How long connecting, and each exchange with the server, may take. A relay that hangs must not hold up the outbox
    // relay, which sends one row after another.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class SenderOptions
{
    // The address mail is sent from. It has to be one the relay allows (usually an address on a verified domain).
    public string Address { get; set; } = string.Empty;

    public string Name { get; set; } = "ChatNET";
}

public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        // Nothing to check while the feature is off. Settings left over from a half-configured deployment are not an error
        // until a host is named.
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        var smtp = options.Smtp;

        if (smtp.Port is < 1 or > 65535)
            failures.Add("Email:Smtp:Port must be between 1 and 65535.");

        if (!Enum.IsDefined(smtp.Security))
            failures.Add("Email:Smtp:Security must be StartTls, SslOnConnect or None.");

        if (smtp.Timeout <= TimeSpan.Zero)
            failures.Add("Email:Smtp:Timeout must be greater than zero.");

        // A login is a user name and a password together. Half of one would only show as a refusal at the first send.
        if ((smtp.UserName.Length > 0) != (smtp.Password.Length > 0))
            failures.Add("Email:Smtp:UserName and Email:Smtp:Password must both be set, or neither.");

        // The login would cross the network in the clear.
        if (smtp.Security == SmtpSecurity.None && smtp.UserName.Length > 0)
            failures.Add("Email:Smtp:Security None cannot be used with a login; use StartTls or SslOnConnect.");

        if (!MailboxAddress.TryParse(options.From.Address, out _))
            failures.Add("Email:From:Address must be set to a valid email address when Email:Smtp:Host is set.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
