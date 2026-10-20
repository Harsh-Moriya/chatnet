using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Tests.Conversations;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Common;

// What the application writes to its log, read through a capturing logger provider. Two things
// matter in production: that nothing expected shows up as an error (an alert on the Error level
// would fire all day), and that nothing sensitive is written down.
[Collection(IntegrationTestCollection.Name)]
public class LoggingTests
{
    private readonly ChatApiFactory _factory;

    public LoggingTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_refused_hub_call_is_one_warning_and_no_error()
    {
        var capture = new CapturingLoggerProvider();
        using var host = HostWith(capture);
        var alice = await _factory.RegisterUserAsync("alice");
        await using var client = await HubClient.ConnectAsync(host, alice);

        await Assert.ThrowsAsync<HubException>(() => client.StartTypingAsync(Guid.NewGuid()));

        AssertNoErrors(capture);
        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains("RefusedCallLoggingFilter"));
        Assert.Contains("StartTyping", warning);
        Assert.Contains(alice.Id.ToString(), warning);
    }

    // Both requests miss the lookup and insert, so one loses to the unique index. EF Core logs that
    // as two errors, although the handler expects it and returns the winner's conversation.
    [Fact]
    public async Task Losing_a_unique_index_race_that_the_code_handles_logs_no_error()
    {
        var capture = new CapturingLoggerProvider();
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var insertResults = new ConcurrentBag<bool>();
        using var barrier = new Barrier(2);
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.AddProvider(capture));
            services.RemoveAll<IConversationRepository>();
            services.AddScoped<IConversationRepository>(provider => new BarrierConversationRepository(
                ActivatorUtilities.CreateInstance<ConversationRepository>(provider), barrier, insertResults));
        }));
        using var aliceClient = Authorized(host, alice);
        using var bobClient = Authorized(host, bob);

        var responses = await Task.WhenAll(aliceClient.PostDirectAsync(bob.Id), bobClient.PostDirectAsync(alice.Id));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(new[] { false, true }, insertResults.Order().ToArray());
        AssertNoErrors(capture);
    }

    // Events are forwarded to every other registered provider, so a default console provider would print
    // each line twice, once as text and once as JSON.
    [Fact]
    public void Serilog_is_the_only_writer_the_host_registers()
    {
        var providers = _factory.Services.GetServices<ILoggerProvider>().Select(p => p.GetType().FullName);

        Assert.Empty(providers);
    }

    [Fact]
    public async Task A_request_line_carries_the_user_but_never_the_query_string()
    {
        var capture = new CapturingLoggerProvider();
        using var host = HostWith(capture);
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = Authorized(host, alice);
        var secret = $"secret-{Guid.NewGuid():N}";

        var response = await client.GetAsync($"/api/conversations?access_token={secret}&search={secret}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = RequestLine(capture, "GET", "/api/conversations", 200);
        Assert.Contains($"UserId={alice.Id}", line);
        Assert.DoesNotContain(capture.Entries, e => e.Contains(secret));
    }

    // The SignalR client sends its token in the query string of the hub requests, so these are the
    // requests most likely to leak it.
    [Fact]
    public async Task A_token_in_the_query_of_a_hub_request_is_not_logged()
    {
        var capture = new CapturingLoggerProvider();
        using var host = HostWith(capture);
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = host.CreateClient();

        var response = await client.PostAsync(
            $"/hubs/chat/negotiate?negotiateVersion=1&access_token={alice.Token}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestLine(capture, "POST", "/hubs/chat/negotiate", 200);
        Assert.DoesNotContain(capture.Entries, e => e.Contains(alice.Token));
    }

    [Fact]
    public async Task A_password_is_not_logged()
    {
        var capture = new CapturingLoggerProvider();
        using var host = HostWith(capture);
        using var client = host.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var password = $"Aa1-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            userName = $"logs_{suffix}",
            displayName = "Logs",
            email = $"logs_{suffix}@example.test",
            password,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestLine(capture, "POST", "/api/auth/register", 200);
        Assert.DoesNotContain(capture.Entries, e => e.Contains(password));
    }

    // Orchestrators call /health every few seconds; one line each would bury everything else.
    [Fact]
    public async Task Health_probes_are_not_logged()
    {
        var capture = new CapturingLoggerProvider();
        using var host = HostWith(capture);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(capture.Entries, e => e.Contains("responded"));
    }

    // The request line the logging middleware writes once a request is done. The parts are matched
    // separately so the test does not depend on the exact wording of the line.
    private static string RequestLine(CapturingLoggerProvider capture, string method, string path, int status)
        => Assert.Single(capture.Entries, e =>
            e.Contains($"responded {status}") && e.Contains(method) && e.Contains(path));

    private static void AssertNoErrors(CapturingLoggerProvider capture)
    {
        var errors = capture.Entries.Where(e => e.StartsWith("[Error]")).Select(e => e.Length > 900 ? e[..300] + " ... " + e[^600..] : e).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private WebApplicationFactory<Program> HostWith(CapturingLoggerProvider capture)
        => _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }
}
