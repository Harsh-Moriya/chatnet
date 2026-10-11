using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Auth;

// Login and registration are the endpoints an attacker hammers (password guessing, mass account
// creation), so they are limited per client address. The shared test host uses a very high
// limit so ordinary tests are never throttled; these tests build their own host with a tight one.
[Collection(IntegrationTestCollection.Name)]
public class RateLimitingTests
{
    private readonly ChatApiFactory _factory;

    public RateLimitingTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_attempts_beyond_the_limit_get_429_with_retry_information()
    {
        using var host = HostWithLimit(permitLimit: 3);
        using var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(client)).StatusCode);

        var blocked = await WrongLoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("application/problem+json", blocked.Content.Headers.ContentType?.MediaType);

        var problem = await blocked.ReadProblemAsync();
        Assert.Equal(429, problem.Status);
        Assert.Equal("Too Many Requests", problem.Title);
        Assert.Contains("Try again in", problem.Detail);

        var retryAfter = Assert.Single(blocked.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter), 1, 60);
    }

    [Fact]
    public async Task Registration_is_limited_too()
    {
        using var host = HostWithLimit(permitLimit: 2);
        using var client = host.CreateClient();

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(client)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await RegisterAsync(client)).StatusCode);
    }

    [Fact]
    public async Task One_client_exhausting_its_limit_does_not_block_another()
    {
        using var host = HostWithLimit(permitLimit: 2);
        using var abuser = ClientFrom(host, "10.0.0.1");
        using var bystander = ClientFrom(host, "10.0.0.2");

        await WrongLoginAsync(abuser);
        await WrongLoginAsync(abuser);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongLoginAsync(abuser)).StatusCode);

        // A different address still has its full allowance and gets a normal answer.
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(bystander)).StatusCode);
    }

    [Fact]
    public async Task Requests_are_accepted_again_once_the_window_has_passed()
    {
        using var host = HostWithLimit(permitLimit: 1, windowSeconds: 1);
        using var client = host.CreateClient();

        await WrongLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongLoginAsync(client)).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(client)).StatusCode);
    }

    [Fact]
    public async Task Other_endpoints_are_not_limited()
    {
        using var host = HostWithLimit(permitLimit: 1);
        var user = await _factory.RegisterUserAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/conversations")).StatusCode);
        }
    }

    [Theory]
    [InlineData("RateLimiting:Auth:PermitLimit", "0")]
    [InlineData("RateLimiting:Auth:WindowSeconds", "0")]
    public void Startup_fails_when_the_limit_settings_are_not_positive(string key, string value)
    {
        using var host = HostWith(new Dictionary<string, string?> { [key] = value });

        var failure = Record.Exception(() => host.CreateClient());

        Assert.NotNull(failure);
        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        Assert.Contains(key.Split(':').Last(), string.Join(" | ", messages));
    }

    private WebApplicationFactory<Program> HostWithLimit(int permitLimit, int windowSeconds = 60) =>
        HostWith(new Dictionary<string, string?>
        {
            ["RateLimiting:Auth:PermitLimit"] = permitLimit.ToString(),
            ["RateLimiting:Auth:WindowSeconds"] = windowSeconds.ToString(),
        });

    private WebApplicationFactory<Program> HostWith(Dictionary<string, string?> settings) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
        });

    private static HttpClient ClientFrom(WebApplicationFactory<Program> host, string ipAddress)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ipAddress);
        return client;
    }

    private static Task<HttpResponseMessage> WrongLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/login", new
        {
            email = $"nobody_{Guid.NewGuid():N}@example.test",
            password = "Wrong1password",
        });

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        return client.PostAsJsonAsync("/api/auth/register", new
        {
            userName = $"rl_{suffix}",
            displayName = "Rate Limit",
            email = $"rl_{suffix}@example.test",
            password = "Aa1" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
        });
    }
}
