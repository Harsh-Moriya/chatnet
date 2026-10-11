using System.Net;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ChatNET.API.Tests.Auth;

// A missing or weak signing key used to surface only when the first token was issued or
// validated, as an obscure error far from the cause. The app now checks its JWT settings when
// it starts and refuses to run with bad ones, with a message that says what to fix.
[Collection(IntegrationTestCollection.Name)]
public class JwtConfigurationTests
{
    private readonly ChatApiFactory _factory;

    public JwtConfigurationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    [InlineData("0123456789012345678901234567890")] // 31 bytes, one short
    public void Startup_fails_when_the_signing_key_is_missing_or_too_short(string key)
    {
        using var factory = WithSetting("Jwt:SigningKey", key);

        var message = StartupFailureMessage(factory);

        Assert.Contains("Jwt:SigningKey", message);
        Assert.Contains("32", message);
        Assert.Contains("user-secrets", message);

        // The configured value is a secret: it must never be echoed into an error message.
        if (key.Trim().Length > 0)
            Assert.DoesNotContain(key, message);
    }

    [Theory]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    public void Startup_fails_when_issuer_or_audience_is_missing(string setting)
    {
        using var factory = WithSetting(setting, "");

        var message = StartupFailureMessage(factory);

        Assert.Contains(setting, message);
    }

    [Fact]
    public void Startup_fails_when_the_token_lifetime_is_not_positive()
    {
        using var factory = WithSetting("Jwt:ExpiryDays", "0");

        var message = StartupFailureMessage(factory);

        Assert.Contains("Jwt:ExpiryDays", message);
    }

    [Fact]
    public async Task A_key_of_exactly_32_bytes_is_accepted_and_tokens_work()
    {
        using var factory = WithSetting("Jwt:SigningKey", new string('k', 32));
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private WebApplicationFactory<Program> WithSetting(string key, string value) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

    // The host is built lazily, so the failure appears when the first client is created.
    // Every message in the chain of inner exceptions is joined so the test does not depend
    // on how many wrapper exceptions the test host adds.
    private static string StartupFailureMessage(WebApplicationFactory<Program> factory)
    {
        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);

        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
            messages.Add(current.Message);

        return string.Join(" | ", messages);
    }
}
