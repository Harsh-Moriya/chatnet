using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class LoginValidationTests
{
    private readonly ChatApiFactory _factory;

    public LoginValidationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Checking a password costs time in proportion to its length, so an unbounded one would let a
    // single request burn server time.
    [Fact]
    public async Task The_password_has_an_upper_length()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "someone@example.test", password = new string('x', 129) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_email_has_an_upper_length()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = new string('a', 255 - 13) + "@example.test", password = "Aa1xxxxxxxx" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
