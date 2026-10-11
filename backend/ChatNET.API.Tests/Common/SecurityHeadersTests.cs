using System.Net;
using Microsoft.AspNetCore.Hosting;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Common;

// Response headers that tell browsers and caches how to treat what the API sends. None of them
// changes what the API does; they close off whole classes of browser-side misuse.
[Collection(IntegrationTestCollection.Name)]
public class SecurityHeadersTests
{
    private readonly ChatApiFactory _factory;

    public SecurityHeadersTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/conversations")]
    [InlineData("/api/does-not-exist")]
    public async Task Every_response_carries_the_standard_headers(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
    }

    [Fact]
    public async Task Error_responses_carry_them_too()
    {
        using var client = _factory.CreateClient();

        var unauthorized = await client.GetAsync("/api/conversations");
        var badRequest = await client.PostAsync("/api/auth/login", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.Equal("nosniff", Header(unauthorized, "X-Content-Type-Options"));
        Assert.Equal("nosniff", Header(badRequest, "X-Content-Type-Options"));
    }

    // The exception handler rewrites the response when something unhandled goes wrong. The headers are
    // added when the response starts, so they are still there.
    [Fact]
    public async Task An_unhandled_error_still_carries_the_headers()
    {
        var user = await _factory.RegisterUserAsync("boom");
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<ChatNET.API.Conversations.Persistence.IConversationRepository>(
                _ => throw new InvalidOperationException("Simulated failure."))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-store", Header(response, "Cache-Control"));
    }

    // Data about a signed-in user must not be kept by a shared cache or the browser's back button.
    [Theory]
    [InlineData("/api/conversations")]
    [InlineData("/health")]
    public async Task Api_responses_are_not_cacheable(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal("no-store", Header(response, "Cache-Control"));
    }

    [Fact]
    public async Task Authenticated_data_is_not_cacheable_either()
    {
        var user = await _factory.RegisterUserAsync("cache");
        using var client = _factory.CreateClientFor(user);

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", Header(response, "Cache-Control"));
    }

    // ##### HSTS #####

    [Fact]
    public async Task Production_tells_browsers_to_use_https_for_a_year()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = host.CreateClient();
        client.BaseAddress = new Uri("https://api.example.test");

        var response = await client.GetAsync("/health");

        var hsts = Header(response, "Strict-Transport-Security");
        Assert.NotNull(hsts);
        Assert.Contains("max-age=31536000", hsts);
    }

    [Fact]
    public async Task Hsts_is_never_sent_over_plain_http()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = host.CreateClient();
        client.BaseAddress = new Uri("http://api.example.test");

        var response = await client.GetAsync("/health");

        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    // Development runs on http://localhost, and a browser that remembered HSTS for localhost would
    // refuse every other local project that does not use https.
    [Fact]
    public async Task Development_does_not_send_hsts()
    {
        using var client = _factory.CreateClient();
        client.BaseAddress = new Uri("https://api.example.test");

        var response = await client.GetAsync("/health");

        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    // ##### Request size #####

    // The default limit is 30 MB. The largest legitimate request here is a message of 4,000
    // characters or a group of 100 ids, a few kilobytes, so a larger body is refused before it is read.
    // The in-memory test server does not enforce Kestrel's limit, so this checks the setting.
    [Fact]
    public void Request_bodies_are_limited_to_one_megabyte()
    {
        var limits = _factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

        Assert.Equal(1_048_576, limits.MaxRequestBodySize);
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
            return string.Join(",", values);

        return null;
    }
}
