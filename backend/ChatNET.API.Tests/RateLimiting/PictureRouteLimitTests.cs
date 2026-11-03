using System.Net;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.RateLimiting;

// The route that serves pictures needs no token, because an <img> cannot send one, and it reads the object store for every request,
// also for an address that holds nothing. So it is limited per client address (D-289), and a miss is an answer and not an event
// worth a warning in the log.
[Collection(IntegrationTestCollection.Name)]
public class PictureRouteLimitTests
{
    private readonly ChatApiFactory _factory;

    public PictureRouteLimitTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> HostWith(int permitLimit, CapturingLoggerProvider? capture = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Avatars:PermitLimit"] = permitLimit.ToString(),
                ["RateLimiting:Avatars:WindowSeconds"] = "60",
            }));
            if (capture is not null) builder.ConfigureLogging(logging => logging.AddProvider(capture));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
        });

    private static HttpClient ClientAt(WebApplicationFactory<Program> host, string address)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, address);
        return client;
    }

    private static string Missing() => $"/api/avatars/users/{Guid.NewGuid()}/{Guid.NewGuid()}";

    // ##### The limit #####

    [Fact]
    public async Task Requests_beyond_the_limit_are_refused_whether_or_not_there_was_a_picture_to_find()
    {
        using var host = HostWith(permitLimit: 3);
        using var client = ClientAt(host, "10.6.6.6");

        // Misses count: they are what an address that makes up ids sends.
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Missing())).StatusCode);

        var refused = await client.GetAsync(Missing());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Retry-After", refused.Headers.Select(h => h.Key));
        // A refusal is not something a browser may keep, as it may keep a picture.
        Assert.True(refused.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task A_picture_that_exists_counts_too_and_is_refused_once_the_address_is_over_the_limit()
    {
        var alice = await _factory.RegisterUserAsync("picture");
        using var owner = _factory.CreateClientFor(alice);
        var address = await owner.SetAvatarAsync();
        using var host = HostWith(permitLimit: 2);
        using var client = ClientAt(host, "10.6.6.7");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(address)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(address)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(address)).StatusCode);
    }

    [Fact]
    public async Task One_address_using_up_its_allowance_does_not_stop_another()
    {
        using var host = HostWith(permitLimit: 1);
        using var abuser = ClientAt(host, "10.6.6.8");
        using var bystander = ClientAt(host, "10.6.6.9");

        await abuser.GetAsync(Missing());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await abuser.GetAsync(Missing())).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await bystander.GetAsync(Missing())).StatusCode);
    }

    [Fact]
    public async Task The_other_endpoints_do_not_share_the_count_of_the_pictures()
    {
        var alice = await _factory.RegisterUserAsync("picture");
        using var host = HostWith(permitLimit: 1);
        using var client = ClientAt(host, "10.6.6.10");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);

        await client.GetAsync(Missing());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(Missing())).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/conversations")).StatusCode);
    }

    // A route added under /api/avatars later would be open to unlimited reads of the store unless somebody remembered to limit it.
    [Fact]
    public void Every_picture_route_has_the_limit()
    {
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/avatars/", StringComparison.Ordinal) == true)
            .ToList();

        Assert.True(routes.Count >= 2, "Expected the route for people and the route for groups.");
        Assert.All(routes, e =>
            Assert.Equal("avatars", e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName));
    }

    // ##### A miss in the log #####

    [Fact]
    public async Task A_picture_that_is_not_there_is_not_a_warning()
    {
        var capture = new CapturingLoggerProvider();
        using var host = HostWith(permitLimit: 1000, capture);
        using var client = ClientAt(host, "10.6.6.11");

        // The first request of a host logs a warning of its own (no https port for the redirect), which is not about a miss.
        await client.GetAsync("/health");
        var before = capture.Entries.Count;

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Missing())).StatusCode);

        // Anyone may ask for any address, so each miss is an ordinary answer and not an event for somebody to read.
        Assert.DoesNotContain(capture.Entries.Skip(before), e => e.StartsWith("[Warning]") || e.StartsWith("[Error]"));
    }

    [Fact]
    public async Task A_picture_that_is_not_there_still_answers_with_the_problem_shape_and_may_not_be_kept()
    {
        using var host = HostWith(permitLimit: 1000);
        using var client = ClientAt(host, "10.6.6.12");

        var response = await client.GetAsync(Missing());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(404, problem.Status);
        Assert.Equal("Not Found", problem.Title);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }
}
