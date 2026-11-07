namespace ChatNET.API.Email;

// A message that was not delivered. An SMTP server's refusal often repeats the recipient's address, and this exception's
// text ends up in the outbox row's last error and in the log, so the message here is made by the sender from a fixed set
// ("SMTP 550", "SMTP timeout") and the library's own exception is never attached.
public sealed class EmailDeliveryException : Exception
{
    public EmailDeliveryException(string message, bool isPermanent) : base(message) =>
        IsPermanent = isPermanent;

    // True when the server refused the message for good (an SMTP 5xx), so sending it again will be refused again.
    public bool IsPermanent { get; }
}
