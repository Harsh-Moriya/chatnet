using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Common;

// The frontend shows ProblemDetails.detail to the user verbatim, so for a validation failure
// it has to read like a sentence, not like a serialised exception.
[Collection(IntegrationTestCollection.Name)]
public class ValidationErrorTests
{
    private readonly ChatApiFactory _factory;

    public ValidationErrorTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Weak_password_gives_a_readable_detail()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            userName = $"weak_{Guid.NewGuid():N}"[..16],
            displayName = "Weak Password",
            email = $"weak_{Guid.NewGuid():N}@example.test",
            password = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();

        Assert.Equal("Validation Failed", problem.Title);
        Assert.Contains("at least 8 characters", problem.Detail);
        AssertReadable(problem.Detail);
    }

    [Fact]
    public async Task Several_failures_are_all_reported_in_one_readable_detail()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            userName = "",
            displayName = "",
            email = "not-an-email",
            password = "",
        });

        var problem = await response.ReadProblemAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("email", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("password", problem.Detail, StringComparison.OrdinalIgnoreCase);
        AssertReadable(problem.Detail);
    }

    [Fact]
    public async Task Field_errors_stay_available_per_field()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            userName = "valid_name",
            displayName = "Valid Name",
            email = "valid@example.test",
            password = "short",
        });

        var problem = await response.ReadProblemAsync();

        Assert.NotNull(problem.Errors);
        var passwordErrors = problem.Errors.Value.GetProperty("Password");
        Assert.True(passwordErrors.GetArrayLength() > 0);
        Assert.False(problem.Errors.Value.TryGetProperty("Email", out _));
    }

    [Fact]
    public async Task Validation_on_another_endpoint_is_readable_too()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClientFor(user);

        var response = await client.GetAsync("/api/users?query=a");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("at least 2 characters", problem.Detail);
        AssertReadable(problem.Detail);
    }

    // None of the markers of FluentValidation's default exception message may reach the user.
    private static void AssertReadable(string? detail)
    {
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.DoesNotContain("Severity", detail);
        Assert.DoesNotContain("Validation failed", detail);
        Assert.DoesNotContain("--", detail);
        Assert.DoesNotContain("\r", detail);
        Assert.DoesNotContain("\n", detail);
    }
}
