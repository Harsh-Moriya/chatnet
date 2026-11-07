using System.Diagnostics;
using ChatNET.API.Email;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Email;

// The sender is checked against a real mail catcher for what arrives, and against a scripted server for what a catcher will
// not do (a refusal for good, a request to try later, silence). Whatever the server says, the exception that comes out never
// repeats its words: SMTP replies often quote the recipient, and the text of the exception reaches the log.
[Collection(IntegrationTestCollection.Name)]
public class SmtpEmailSenderTests
{
    private readonly ChatApiFactory _factory;

    public SmtpEmailSenderTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static string NewAddress() => $"recipient_{Guid.NewGuid():N}@example.test";

    private static SmtpEmailSender SenderFor(int port, TimeSpan? timeout = null) => new(Options.Create(new EmailOptions
    {
        Smtp = new SmtpOptions { Host = "127.0.0.1", Port = port, Security = SmtpSecurity.None, Timeout = timeout ?? TimeSpan.FromSeconds(5) },
        From = new SenderOptions { Address = "noreply@example.test", Name = "ChatNET" },
    }));

    private static EmailMessage MessageTo(string address) => new(address, "A subject", "The text part.", "<p>The <b>HTML</b> part.</p>");

    [Fact]
    public async Task A_message_arrives_with_both_parts_the_subject_and_the_configured_sender()
    {
        var address = NewAddress();
        var sender = _factory.Services.GetRequiredService<IEmailSender>();

        await sender.SendAsync(MessageTo(address), CancellationToken.None);

        var mail = Assert.Single(await _factory.Mail.WaitForAsync(address));
        Assert.Equal("A subject", mail.Subject);
        Assert.Equal(address, Assert.Single(mail.To).Address);
        Assert.Equal(ChatApiFactory.MailFromAddress, mail.From.Address);
        Assert.Equal("ChatNET", mail.From.Name);
        Assert.Contains("The text part.", mail.Text);
        Assert.Contains("<b>HTML</b>", mail.Html);
    }

    [Fact]
    public async Task A_refusal_for_good_is_permanent_and_does_not_repeat_what_the_server_said()
    {
        await using var server = new FakeSmtpServer { RecipientReply = "550 5.1.1 <{0}> no such user here" };
        var address = NewAddress();

        var failure = await Assert.ThrowsAsync<EmailDeliveryException>(
            () => SenderFor(server.Port).SendAsync(MessageTo(address), CancellationToken.None));

        Assert.True(failure.IsPermanent);
        Assert.Equal("SMTP 550", failure.Message);
        Assert.DoesNotContain(address, failure.ToString());
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task A_request_to_try_later_is_not_permanent_and_does_not_repeat_what_the_server_said()
    {
        await using var server = new FakeSmtpServer { RecipientReply = "451 4.3.0 <{0}> try again later" };
        var address = NewAddress();

        var failure = await Assert.ThrowsAsync<EmailDeliveryException>(
            () => SenderFor(server.Port).SendAsync(MessageTo(address), CancellationToken.None));

        Assert.False(failure.IsPermanent);
        Assert.Equal("SMTP 451", failure.Message);
        Assert.DoesNotContain(address, failure.ToString());
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task A_server_that_never_answers_ends_as_a_timeout_that_can_be_tried_again()
    {
        await using var server = new FakeSmtpServer { Silent = true };
        var clock = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<EmailDeliveryException>(
            () => SenderFor(server.Port, TimeSpan.FromMilliseconds(300)).SendAsync(MessageTo(NewAddress()), CancellationToken.None));

        Assert.False(failure.IsPermanent);
        Assert.Equal("SMTP timeout", failure.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_a_connection_failure_that_can_be_tried_again()
    {
        int closedPort;
        await using (var server = new FakeSmtpServer())
            closedPort = server.Port;

        var failure = await Assert.ThrowsAsync<EmailDeliveryException>(
            () => SenderFor(closedPort).SendAsync(MessageTo(NewAddress()), CancellationToken.None));

        Assert.False(failure.IsPermanent);
        Assert.Equal("SMTP connection failed", failure.Message);
    }

    // The host stopping is not a failed delivery: it must reach the caller as a cancellation, so the row is tried again later
    // and not counted as an error.
    [Fact]
    public async Task The_callers_own_cancellation_is_not_turned_into_a_delivery_failure()
    {
        await using var server = new FakeSmtpServer { Silent = true };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SenderFor(server.Port, TimeSpan.FromSeconds(30)).SendAsync(MessageTo(NewAddress()), cancel.Token));
    }

    [Fact]
    public async Task Sending_with_email_off_is_a_programming_error_and_not_a_delivery_failure()
    {
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(MessageTo(NewAddress()), CancellationToken.None));
    }
}
