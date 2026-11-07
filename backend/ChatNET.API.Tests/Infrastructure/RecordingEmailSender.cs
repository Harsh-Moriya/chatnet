using System.Collections.Concurrent;
using ChatNET.API.Email;

namespace ChatNET.API.Tests.Infrastructure;

// An email sender for a host that must not reach a mail server: it keeps what it was asked to send, and fails when a test says so,
// to play a relay that is down or a server that refuses.
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> Sent => _sent.ToArray();

    // The next call throws this, once.
    public void FailNext(Exception failure) => _failures.Enqueue(failure);

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (_failures.TryDequeue(out var failure))
            throw failure;

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
