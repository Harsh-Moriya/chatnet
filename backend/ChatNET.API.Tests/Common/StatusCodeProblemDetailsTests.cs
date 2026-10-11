using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Common;

// Errors raised by exceptions already come back as ProblemDetails. These tests cover the other
// kind: responses the framework produces from a bare status code (authentication failures,
// unknown routes, wrong HTTP method, wrong content type), which used to have an empty body.
// A client should be able to treat every error response the same way.
[Collection(IntegrationTestCollection.Name)]
public class StatusCodeProblemDetailsTests
{
    private readonly ChatApiFactory _factory;

    public StatusCodeProblemDetailsTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Missing_token_returns_401_problem_details()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/conversations");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Invalid_token_returns_401_problem_details()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");

        var response = await client.GetAsync("/api/conversations");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Unknown_route_returns_404_problem_details()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/does-not-exist");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Not Found");
    }

    [Fact]
    public async Task Wrong_http_method_returns_405_problem_details()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/login");

        await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, "Method Not Allowed");
    }

    [Fact]
    public async Task Unsupported_content_type_returns_415_problem_details()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/login",
            new StringContent("x=1", Encoding.UTF8, "application/x-www-form-urlencoded"));

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "Unsupported Media Type");
    }

    // The error handler already writes a body for domain exceptions. Adding status code pages
    // must not write a second one on top of it, or the JSON would be invalid.
    [Fact]
    public async Task Domain_exception_responses_still_have_exactly_one_valid_body()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClientFor(user);

        var response = await client.PostAsJsonAsync("/api/conversations/direct",
            new { otherUserId = Guid.NewGuid() });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Not Found");
        var problem = await response.ReadProblemAsync();
        Assert.Contains("was not found", problem.Detail);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedTitle)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.ReadProblemAsync();
        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.Equal(expectedTitle, problem.Title);
    }
}
