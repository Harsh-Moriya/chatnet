using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace ChatNET.API.Tests.Common;

// What a trace contains, read from an in-memory exporter added to the real tracing setup. The
// tests check two things: the spans that should be there are linked into one trace, and nothing a
// user typed or a token the client sent is copied onto any span.
[Collection(IntegrationTestCollection.Name)]
public class TracingTests
{
    private const string ServerSource = "Microsoft.AspNetCore";
    private const string SignalRSource = "Microsoft.AspNetCore.SignalR.Server";

    private readonly ChatApiFactory _factory;

    public TracingTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Sending_a_message_is_one_trace_from_the_request_through_the_handler_to_the_database()
    {
        var exported = new ExportedActivities();
        using var host = HostExporting(exported);
        var (alice, conversation) = await DirectChatAsync();
        using var client = Authorized(host, alice);
        var path = $"/api/conversations/{conversation}/messages";

        await client.SendMessageAsync(conversation, "hello");

        var spans = await exported.WaitForAsync(IsRequestTo(path));
        var request = Assert.Single(spans, IsRequestTo(path));
        var handler = Assert.Single(spans, a => a.Source.Name == "ChatNET" && a.OperationName == "SendMessageCommand");
        var database = spans.Where(a => a.Source.Name == "Npgsql" && a.TraceId == request.TraceId).ToList();

        Assert.Equal(request.TraceId, handler.TraceId);
        Assert.NotEmpty(database);
        Assert.Contains(spans, a => a.Source.Name == "ChatNET" && a.OperationName == "SendMessageCommand"
                                    && IsDescendantOf(a, request, spans));
        Assert.All(database, span => Assert.True(IsDescendantOf(span, request, spans)));
    }

    [Fact]
    public async Task No_span_carries_the_text_of_a_message()
    {
        var exported = new ExportedActivities();
        using var host = HostExporting(exported);
        var (alice, conversation) = await DirectChatAsync();
        using var client = Authorized(host, alice);
        var secret = $"secret-{Guid.NewGuid():N}";

        await client.SendMessageAsync(conversation, secret);

        var spans = await exported.WaitForAsync(IsRequestTo($"/api/conversations/{conversation}/messages"));
        Assert.True(spans.Count >= 3, "The request, the handler and the database spans should all have been exported.");
        Assert.DoesNotContain(spans, span => TagValues(span).Any(value => value.Contains(secret)));
    }

    // The SignalR client sends its token in the query string, and the instrumentation records the
    // query string as a tag unless told not to.
    [Fact]
    public async Task A_token_in_the_query_string_is_not_copied_onto_a_span()
    {
        var exported = new ExportedActivities();
        using var host = HostExporting(exported);
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = host.CreateClient();

        var response = await client.PostAsync(
            $"/hubs/chat/negotiate?negotiateVersion=1&access_token={alice.Token}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var spans = await exported.WaitForAsync(IsRequestTo("/hubs/chat/negotiate"));
        Assert.Contains(spans, IsRequestTo("/hubs/chat/negotiate"));
        Assert.DoesNotContain(spans, span => TagValues(span).Any(value => value.Contains(alice.Token)));
        Assert.DoesNotContain(spans, span => span.GetTagItem("url.query") is not null);
    }

    [Fact]
    public async Task Health_probes_are_not_traced()
    {
        var exported = new ExportedActivities();
        using var host = HostExporting(exported);
        using var client = host.CreateClient();

        await client.GetAsync("/health");
        await client.GetAsync("/api/this-route-does-not-exist");

        var spans = await exported.WaitForAsync(IsRequestTo("/api/this-route-does-not-exist"));
        Assert.Contains(spans, IsRequestTo("/api/this-route-does-not-exist"));
        Assert.DoesNotContain(spans, IsRequestTo("/health"));
    }

    [Fact]
    public async Task A_hub_call_is_traced_by_the_signalr_server()
    {
        var exported = new ExportedActivities();
        using var host = HostExporting(exported);
        var alice = await _factory.RegisterUserAsync("alice");
        await using var hub = await HubClient.ConnectAsync(host, alice);

        await Assert.ThrowsAsync<HubException>(() => hub.StartTypingAsync(Guid.NewGuid()));

        var spans = await exported.WaitForAsync(a => a.Source.Name == SignalRSource && a.DisplayName.Contains("StartTyping"));
        Assert.Contains(spans, a => a.Source.Name == SignalRSource && a.DisplayName.Contains("StartTyping"));
    }

    private WebApplicationFactory<Program> HostExporting(ExportedActivities exported)
        => _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureOpenTelemetryTracerProvider((_, tracing) => tracing.AddInMemoryExporter(exported))));

    private async Task<(TestUser User, Guid Conversation)> DirectChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        return (alice, (await client.CreateDirectAsync(bob.Id)).IdOf());
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    private static Predicate<Activity> IsRequestTo(string path)
        => a => a.Source.Name == ServerSource && a.GetTagItem("url.path")?.ToString() == path;

    private static IEnumerable<string> TagValues(Activity activity)
        => activity.TagObjects.Select(tag => tag.Value?.ToString() ?? string.Empty);

    private static bool IsDescendantOf(Activity span, Activity ancestor, IReadOnlyList<Activity> all)
    {
        for (var parentId = span.ParentSpanId; parentId != default;)
        {
            if (parentId == ancestor.SpanId)
                return true;

            var parent = all.FirstOrDefault(a => a.SpanId == parentId);
            if (parent is null)
                return false;

            parentId = parent.ParentSpanId;
        }

        return false;
    }
}
