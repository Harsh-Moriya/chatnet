using System.Net;
using System.Text;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace ChatNET.API.Tests.Common;

// A malformed request is the client's mistake, so it must produce 400 with a ProblemDetails
// body, never a 500. The tests run in Development, where ASP.NET throws on bad input, and in
// Production, where it used to answer with a bare 400, so both environments are covered.
[Collection(IntegrationTestCollection.Name)]
public class MalformedRequestTests
{
    private readonly ChatApiFactory _factory;

    public MalformedRequestTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Missing_required_query_parameter_returns_400()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClientFor(user);

        var response = await client.GetAsync("/api/users");

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task Invalid_json_body_returns_400()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClientFor(user);

        var response = await client.PostAsync("/api/conversations/direct", Json("{bad"));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task Empty_body_returns_400()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClientFor(user);

        var response = await client.PostAsync("/api/conversations/direct", Json(""));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task Invalid_guid_in_body_returns_400()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClientFor(user);

        var response = await client.PostAsync(
            "/api/conversations/direct", Json("""{"otherUserId":"not-a-guid"}"""));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task Production_environment_also_returns_a_400_problem_details_body()
    {
        var user = await _factory.RegisterUserAsync();

        using var production = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);

        var response = await client.PostAsync("/api/conversations/direct", Json("{bad"));

        await AssertBadRequestAsync(response);
    }

    [Fact]
    public async Task Bad_request_detail_does_not_expose_internal_type_names()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClientFor(user);

        var response = await client.PostAsync("/api/conversations/direct", Json("{bad"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();

        Assert.DoesNotContain("CreateDirectRequest", problem.Detail ?? "");
        Assert.DoesNotContain("Exception", problem.Detail ?? "");
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task AssertBadRequestAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.ReadProblemAsync();
        Assert.Equal(400, problem.Status);
        Assert.Equal("Bad Request", problem.Title);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
    }
}
