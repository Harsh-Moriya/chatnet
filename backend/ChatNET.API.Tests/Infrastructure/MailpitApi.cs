using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ChatNET.API.Tests.Infrastructure;

// Reads what the API sent to the mail catcher. One catcher serves the whole test run, so a test always looks for its own
// mail by recipient (every test uses addresses of its own) and never at "all the mail".
public sealed class MailpitApi : IDisposable
{
    private readonly HttpClient _http;

    public MailpitApi(Uri baseAddress) => _http = new HttpClient { BaseAddress = baseAddress };

    // The mail sent to an address so far, newest first, with the bodies.
    public async Task<IReadOnlyList<ReceivedMail>> SentToAsync(string address)
    {
        var found = await _http.GetFromJsonAsync<SearchResult>($"/api/v1/search?query={Uri.EscapeDataString($"to:\"{address}\"")}")
                    ?? throw new InvalidOperationException("The mail catcher returned nothing.");

        var mails = new List<ReceivedMail>();
        foreach (var summary in found.Messages)
            mails.Add(await _http.GetFromJsonAsync<ReceivedMail>($"/api/v1/message/{summary.Id}")
                      ?? throw new InvalidOperationException("The mail catcher returned nothing."));

        return mails;
    }

    // Waits for the mail that a background step (the outbox relay, or the API) sends, up to a few seconds.
    public async Task<IReadOnlyList<ReceivedMail>> WaitForAsync(string address, int count = 1)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var mails = await SentToAsync(address);
            if (mails.Count >= count || DateTime.UtcNow > until)
                return mails;

            await Task.Delay(100);
        }
    }

    // For the claim that nothing was sent: waits the given time, then looks.
    public async Task<IReadOnlyList<ReceivedMail>> SentToAfterAsync(string address, TimeSpan wait)
    {
        await Task.Delay(wait);
        return await SentToAsync(address);
    }

    public void Dispose() => _http.Dispose();

    private sealed record SearchResult([property: JsonPropertyName("messages")] List<Summary> Messages);

    private sealed record Summary([property: JsonPropertyName("ID")] string Id);
}

public sealed record ReceivedMail(
    [property: JsonPropertyName("ID")] string Id,
    [property: JsonPropertyName("From")] MailAddress From,
    [property: JsonPropertyName("To")] List<MailAddress> To,
    [property: JsonPropertyName("Subject")] string Subject,
    [property: JsonPropertyName("Text")] string Text,
    [property: JsonPropertyName("HTML")] string Html);

public sealed record MailAddress(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Address")] string Address);
