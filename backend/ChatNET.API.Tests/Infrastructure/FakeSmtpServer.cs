using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ChatNET.API.Tests.Infrastructure;

// An SMTP server that answers as a test says, for what a real mail catcher will not do: refuse a recipient for good, ask to
// try later, refuse a login, or say nothing at all. It speaks just enough of the protocol for MailKit to get as far as the
// recipient.
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _messages;

    // What the server says to RCPT TO. "{0}" is replaced with the address, as many servers repeat it in their refusals.
    public string RecipientReply { get; init; } = "250 ok";

    // Accepts the connection and never says anything.
    public bool Silent { get; init; }

    // Offers a login (AUTH PLAIN) and refuses whatever login is given, quoting it, as a relay does for a wrong key.
    public bool RejectLogins { get; init; }

    public FakeSmtpServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // How many messages the server took (answered the end of DATA for).
    public int MessagesReceived => Volatile.Read(ref _messages);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            if (Silent)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
                return;
            }

            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ready");

            string? line;
            while ((line = await reader.ReadLineAsync(_stop.Token)) is not null)
            {
                var command = line.ToUpperInvariant();
                if (command.StartsWith("EHLO") || command.StartsWith("HELO"))
                {
                    if (RejectLogins)
                    {
                        await writer.WriteLineAsync("250-fake");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                    }
                    else
                        await writer.WriteLineAsync("250 fake");
                }
                else if (command.StartsWith("AUTH"))
                    await writer.WriteLineAsync($"535 5.7.8 credentials rejected: {line}");
                else if (command.StartsWith("MAIL FROM"))
                    await writer.WriteLineAsync("250 ok");
                else if (command.StartsWith("RCPT TO"))
                    await writer.WriteLineAsync(string.Format(RecipientReply, line[(line.IndexOf('<') + 1)..line.IndexOf('>')]));
                else if (command.StartsWith("DATA"))
                {
                    await writer.WriteLineAsync("354 go on");
                    while ((line = await reader.ReadLineAsync(_stop.Token)) is not null && line != ".")
                    {
                    }

                    Interlocked.Increment(ref _messages);
                    await writer.WriteLineAsync("250 queued");
                }
                else if (command.StartsWith("QUIT"))
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else
                    await writer.WriteLineAsync("250 ok");
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or the test ended.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}
