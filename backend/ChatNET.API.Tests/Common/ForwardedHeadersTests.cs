using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Common;

// Behind the reverse proxy the API is reached from the proxy's address over plain http. The proxy says who the
// client was and which scheme it used in X-Forwarded-For and X-Forwarded-Proto. Anyone can send those headers, so
// they are believed only from the configured networks. These tests stand in for the proxy: the connection's peer
// address is set with a header (RemoteIpStartupFilter), as it is in the rate limit tests.
[Collection(IntegrationTestCollection.Name)]
public class ForwardedHeadersTests
{
    // Inside and outside the trusted network.
    private const string Proxy = "10.0.0.5";
    private const string Stranger = "192.0.2.50";

    private readonly ChatApiFactory _factory;

    public ForwardedHeadersTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### The client's address #####

    [Fact]
    public async Task The_address_a_trusted_proxy_reports_is_the_one_the_rate_limit_counts()
    {
        using var host = HostWith(trusted: true, permitLimit: 2);
        using var abuser = Via(host, Proxy, forwardedFor: "203.0.113.1");
        using var bystander = Via(host, Proxy, forwardedFor: "203.0.113.2");

        await WrongLoginAsync(abuser);
        await WrongLoginAsync(abuser);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongLoginAsync(abuser)).StatusCode);

        // Same proxy, another client behind it: its own allowance.
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(bystander)).StatusCode);
    }

    // Without this, every client behind the proxy would share one counter and one person's guessing would lock
    // everyone out of signing in.
    [Fact]
    public async Task Clients_behind_the_proxy_are_counted_apart_even_though_they_share_its_address()
    {
        using var host = HostWith(trusted: true, permitLimit: 1);

        for (var client = 1; client <= 5; client++)
        {
            using var http = Via(host, Proxy, forwardedFor: $"203.0.113.{client}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(http)).StatusCode);
        }
    }

    [Fact]
    public async Task An_address_outside_the_trusted_networks_cannot_choose_its_own()
    {
        using var host = HostWith(trusted: true, permitLimit: 2);

        // Every request names a different client; it is still one peer and one counter.
        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
        {
            using var client = Via(host, Stranger, forwardedFor: $"203.0.113.{i}");
            statuses.Add((await WrongLoginAsync(client)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    // The framework trusts the loopback address by default; only what is configured is trusted here.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task The_loopback_address_is_not_trusted_unless_it_is_configured(string loopback)
    {
        using var host = HostWith(trusted: true, permitLimit: 2);

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
        {
            using var client = Via(host, loopback, forwardedFor: $"203.0.113.{i}");
            statuses.Add((await WrongLoginAsync(client)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task With_no_trusted_network_configured_the_headers_are_never_believed()
    {
        using var host = HostWith(trusted: false, permitLimit: 2);

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
        {
            using var client = Via(host, Proxy, forwardedFor: $"203.0.113.{i}");
            statuses.Add((await WrongLoginAsync(client)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    // The address in the chain that the proxy added is the client's; whatever the client wrote before it is not.
    [Fact]
    public async Task Only_the_address_the_proxy_added_counts_not_the_ones_the_client_wrote_before_it()
    {
        using var host = HostWith(trusted: true, permitLimit: 2);

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
        {
            using var client = Via(host, Proxy, forwardedFor: $"198.51.100.{i}, 203.0.113.7");
            statuses.Add((await WrongLoginAsync(client)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    // ##### The scheme #####

    [Fact]
    public async Task A_trusted_proxy_that_says_https_makes_the_request_secure_so_hsts_is_sent_and_nothing_redirects()
    {
        using var host = HostWith(trusted: true, environment: "Production");
        using var client = Via(host, Proxy, forwardedProto: "https", hostName: "chat.example.test");

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=31536000", string.Join(";", response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public async Task A_client_that_says_https_without_being_a_trusted_proxy_is_still_plain_http()
    {
        using var host = HostWith(trusted: true, environment: "Production");
        using var client = Via(host, Stranger, forwardedProto: "https", hostName: "chat.example.test");

        var response = await client.GetAsync("/health");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    // ##### Configuration #####

    [Fact]
    public void Startup_fails_when_a_trusted_network_is_not_in_cidr_form()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks:0"] = "not-a-network" })));

        var failure = Record.Exception(() => host.CreateClient());

        Assert.NotNull(failure);
        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        Assert.Contains("KnownNetworks", string.Join(" | ", messages));
    }

    // ##### Allowed hosts #####

    [Fact]
    public async Task Only_the_configured_host_names_are_answered_and_the_probe_address_still_is()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AllowedHosts"] = "chat.example.test;localhost" })));
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetFromHostAsync(client, "chat.example.test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetFromHostAsync(client, "localhost")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetFromHostAsync(client, "other.example.test")).StatusCode);
    }

    // ##### Helpers #####

    private WebApplicationFactory<Program> HostWith(bool trusted, int permitLimit = 1000, string? environment = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            if (environment is not null)
                builder.UseEnvironment(environment);

            var settings = new Dictionary<string, string?> { ["RateLimiting:Auth:PermitLimit"] = permitLimit.ToString() };
            if (trusted)
                settings["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8";

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
        });

    // A client whose connection comes from `peer` and that carries the headers a proxy would add.
    private static HttpClient Via(
        WebApplicationFactory<Program> host, string peer, string? forwardedFor = null, string? forwardedProto = null, string? hostName = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, peer);
        if (forwardedFor is not null)
            client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);
        if (hostName is not null)
            client.DefaultRequestHeaders.Host = hostName;
        return client;
    }

    private static Task<HttpResponseMessage> WrongLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/login", new
        {
            email = $"nobody_{Guid.NewGuid():N}@example.test",
            password = "Wrong1password",
        });

    private static Task<HttpResponseMessage> GetFromHostAsync(HttpClient client, string hostName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = hostName;
        return client.SendAsync(request);
    }
}
