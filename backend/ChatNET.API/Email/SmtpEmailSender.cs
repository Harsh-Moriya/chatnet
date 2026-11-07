using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ChatNET.API.Email;

// Sends through the configured SMTP relay with MailKit. A connection is opened for each message: mail here is rare (a
// password reset), a long-lived connection would need its own keep-alive and reconnect handling, and a relay closes idle
// ones anyway.
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;

    public SmtpEmailSender(IOptions<EmailOptions> options) => _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Email is off: Email:Smtp:Host is not set.");

        var mime = Build(message);

        // MailKit's own timeout covers each socket operation; this one bounds the whole exchange, so a server that answers
        // slowly but never finishes cannot hold the caller for much longer than the configured time.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Smtp.Timeout * 3);

        try
        {
            using var client = new SmtpClient { Timeout = (int)_options.Smtp.Timeout.TotalMilliseconds };

            await client.ConnectAsync(_options.Smtp.Host, _options.Smtp.Port, Options(_options.Smtp.Security), deadline.Token);

            if (_options.Smtp.UserName.Length > 0)
                await client.AuthenticateAsync(_options.Smtp.UserName, _options.Smtp.Password, deadline.Token);

            await client.SendAsync(mime, deadline.Token);
            await client.DisconnectAsync(true, deadline.Token);
        }
        // The caller's own cancellation (the host stopping) is not a delivery failure.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing of the library's exception is kept: an SMTP reply often quotes the recipient's address.
            throw Describe(ex);
        }
    }

    private MimeMessage Build(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.From.Name, _options.From.Address));
        mime.To.Add(Recipient(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }

    // The address comes from a stored account. One that MimeKit cannot read would fail the same way on every try, and its error text
    // repeats the address, so it is a refusal for good with a fixed text.
    private static MailboxAddress Recipient(string address)
    {
        try
        {
            return MailboxAddress.Parse(address);
        }
        catch (ParseException)
        {
            throw new EmailDeliveryException("The recipient address cannot be used", isPermanent: true);
        }
    }

    private static SecureSocketOptions Options(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(security)),
    };

    // A fixed text per kind of failure. Only an SMTP 5xx is permanent: 4xx means "try later", and so does everything that
    // never reached a verdict (a refused connection, a timeout, a dropped line).
    private static EmailDeliveryException Describe(Exception ex) => ex switch
    {
        SmtpCommandException command => new EmailDeliveryException($"SMTP {(int)command.StatusCode}", (int)command.StatusCode >= 500),
        AuthenticationException => new EmailDeliveryException("SMTP authentication failed", isPermanent: false),
        SslHandshakeException => new EmailDeliveryException("SMTP TLS handshake failed", isPermanent: false),
        OperationCanceledException or TimeoutException => new EmailDeliveryException("SMTP timeout", isPermanent: false),
        SocketException or IOException or ServiceNotConnectedException => new EmailDeliveryException("SMTP connection failed", isPermanent: false),
        _ => new EmailDeliveryException("SMTP error", isPermanent: false),
    };
}
