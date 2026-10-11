using System.Text.Json;

namespace ChatNET.API.Tests.Infrastructure;

// A parsed RFC 7807 ProblemDetails body. Parsing by hand (instead of binding to a framework
// type) means the tests assert on the JSON a client actually receives.
public sealed record Problem(int Status, string? Title, string? Detail, JsonElement Root)
{
    public JsonElement? Errors =>
        Root.TryGetProperty("errors", out var errors) ? errors : null;
}

public static class ProblemDetailsReader
{
    public static async Task<Problem> ReadProblemAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"Expected a ProblemDetails body but the {(int)response.StatusCode} response was empty.");

        var root = JsonDocument.Parse(body).RootElement.Clone();

        return new Problem(
            root.TryGetProperty("status", out var status) ? status.GetInt32() : 0,
            root.TryGetProperty("title", out var title) ? title.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            root);
    }
}
