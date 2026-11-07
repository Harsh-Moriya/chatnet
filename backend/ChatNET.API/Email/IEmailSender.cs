namespace ChatNET.API.Email;

// A message with both a plain text and an HTML body, so a client that does not show HTML still has something to read.
public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    // Hands the message to the SMTP relay. Throws EmailDeliveryException when it could not be delivered; the exception says
    // whether trying again can help.
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
