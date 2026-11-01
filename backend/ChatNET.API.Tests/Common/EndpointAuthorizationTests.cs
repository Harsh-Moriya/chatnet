using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Common;

// Authorization is checked on every endpoint, never assumed. Each feature's endpoints are declared in a group that
// requires a signed-in user, which only works as long as every new group says so. This lists what a production host
// answers without a token, and fails when the list changes: a new open endpoint must be added here on purpose, and a
// group that forgets to require authorization shows up as an unexpected one.
[Collection(IntegrationTestCollection.Name)]
public class EndpointAuthorizationTests
{
    // Sign-in, registration, renewing and ending a session (their credential is the refresh cookie or the password,
    // not a bearer token), the health probe, and the pictures (an <img> cannot send a token; the address is the
    // credential: it holds random ids and a file name that is new for every picture).
    private static readonly string[] OpenInProduction =
    [
        "GET /api/avatars/groups/{ownerId:guid}/{fileId:guid}",
        "GET /api/avatars/users/{ownerId:guid}/{fileId:guid}",
        "GET /health",
        "POST /api/auth/login",
        "POST /api/auth/logout",
        "POST /api/auth/refresh",
        "POST /api/auth/register",
    ];

    private readonly ChatApiFactory _factory;

    public EndpointAuthorizationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void A_production_host_answers_only_the_sign_in_endpoints_and_the_health_probe_without_a_token()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var open = OpenEndpoints(host.Services);

        Assert.Equal(OpenInProduction.Order(), open.Order());
    }

    // The API reference pages exist only in Development, and are open there.
    [Fact]
    public void Development_adds_nothing_open_except_the_api_reference()
    {
        var open = OpenEndpoints(_factory.Services)
            .Where(e => !e.Contains("/openapi/") && !e.Contains("/scalar"))
            .ToList();

        Assert.Equal(OpenInProduction.Order(), open.Order());
    }

    // "METHOD pattern" of every endpoint that does not demand a signed-in user.
    private static List<string> OpenEndpoints(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null || e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                .Select(method => $"{method} {e.RoutePattern.RawText}"))
            .ToList();
}
