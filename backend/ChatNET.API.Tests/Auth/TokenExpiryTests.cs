using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using ChatNET.API.Auth.Services;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ChatNET.API.Tests.Auth;

// A token proves who someone is until it expires. A server that keeps honouring one after that, on a
// request or on an open connection, lets an old session carry on indefinitely.
[Collection(IntegrationTestCollection.Name)]
public class TokenExpiryTests
{
    private readonly ChatApiFactory _factory;

    public TokenExpiryTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_token_that_expired_a_couple_of_minutes_ago_is_rejected()
    {
        var user = await _factory.RegisterUserAsync("expired");
        using var client = ClientWithToken(Mint(user, validFor: TimeSpan.FromMinutes(-2)));

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_with_time_left_is_accepted()
    {
        var user = await _factory.RegisterUserAsync("valid");
        using var client = ClientWithToken(Mint(user, validFor: TimeSpan.FromMinutes(5)));

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_server_closes_a_live_connection_when_its_token_expires()
    {
        var user = await _factory.RegisterUserAsync("closing");
        await using var client = await HubClient.ConnectAsync(_factory, Mint(user, validFor: TimeSpan.FromSeconds(3)));
        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected, client.State);

        var closed = await client.WaitForCloseAsync(TimeSpan.FromSeconds(15));

        Assert.True(closed, "The connection stayed open after its token had expired.");
    }

    [Fact]
    public async Task A_connection_whose_token_has_plenty_of_time_stays_open()
    {
        var user = await _factory.RegisterUserAsync("staying");
        await using var client = await HubClient.ConnectAsync(_factory, Mint(user, validFor: TimeSpan.FromMinutes(10)));

        var closed = await client.WaitForCloseAsync(TimeSpan.FromSeconds(4));

        Assert.False(closed);
        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected, client.State);
    }

    // Builds a token like the one login issues, but with a lifetime the test chooses. A negative
    // lifetime gives a token that has already expired.
    private string Mint(TestUser user, TimeSpan validFor)
    {
        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("display_name", user.DisplayName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            options.Issuer, options.Audience, claims,
            notBefore: now.AddMinutes(-30), expires: now + validFor,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
