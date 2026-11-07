using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Presence;
using ChatNET.API.RateLimiting;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.RateLimiting;

// With the counts in Redis, two servers of the application apply one limit between them, where before each counted for itself
// and a client got up to twice its allowance. And with Redis out of reach the application still
// answers. Signing in is the request used, because every host has it.
[Collection(IntegrationTestCollection.Name)]
public class SharedRateLimitTests
{
    private readonly ChatApiFactory _factory;

    public SharedRateLimitTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Hosts that count under one prefix count together, as two servers of one deployment do.
    private WebApplicationFactory<Program> Server(string prefix, int signInLimit) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Store:KeyPrefix"] = prefix,
                ["RateLimiting:Auth:PermitLimit"] = signInLimit.ToString(),
                ["RateLimiting:Auth:WindowSeconds"] = "60",
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
        });

    private static string NewPrefix() => $"shared{Guid.NewGuid():N}";

    private static HttpClient ClientAt(WebApplicationFactory<Program> host, string address)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, address);
        return client;
    }

    // ##### Two servers #####

    [Fact]
    public async Task Sign_in_attempts_from_one_address_are_counted_across_servers()
    {
        var prefix = NewPrefix();
        using var first = Server(prefix, signInLimit: 3);
        using var second = Server(prefix, signInLimit: 3);
        using var onFirst = ClientAt(first, "10.4.4.4");
        using var onSecond = ClientAt(second, "10.4.4.4");

        // Three attempts between the two of them: a count on each server would have allowed three on each.
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(onFirst)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(onSecond)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(onFirst)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongLoginAsync(onSecond)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongLoginAsync(onFirst)).StatusCode);
    }

    [Fact]
    public async Task Servers_that_count_under_different_prefixes_do_not_see_each_other()
    {
        using var first = Server(NewPrefix(), signInLimit: 1);
        using var second = Server(NewPrefix(), signInLimit: 1);
        using var onFirst = ClientAt(first, "10.5.5.5");
        using var onSecond = ClientAt(second, "10.5.5.5");

        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(onFirst)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(onSecond)).StatusCode);

        // The limit is one, so the second try on either is refused: it is the prefix that kept them apart.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongLoginAsync(onFirst)).StatusCode);
    }

    // ##### Redis out of reach #####

    [Fact]
    public async Task While_redis_cannot_be_reached_requests_go_ahead_and_one_warning_says_so()
    {
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["RateLimiting:Auth:PermitLimit"] = "2", ["RateLimiting:Auth:WindowSeconds"] = "60" }));
            builder.ConfigureLogging(logging => logging.AddProvider(capture));

            // Only the rate limits lose Redis: the rest of the application keeps the real one.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RedisSlidingWindowStore>();
                services.AddSingleton(new RedisSlidingWindowStore(new RedisConnection("127.0.0.1:1,connectTimeout=500,abortConnect=false")));
            });
        });
        using var client = host.CreateClient();

        // Past the limit of two, and none of them is refused.
        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(client)).StatusCode);

        var warnings = capture.Entries.Where(e => e.StartsWith("[Warning]") && e.Contains("Rate limits are not applied")).ToList();
        Assert.Single(warnings);
        Assert.DoesNotContain(capture.Entries, e => e.Contains("127.0.0.1:1"));
    }

    private static Task<HttpResponseMessage> WrongLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/login", new { email = $"nobody_{Guid.NewGuid():N}@example.test", password = "Wrong1password" });
}
