using ChatNET.API.Calls.Ice;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Calls;

// The relay's settings through the host: bound from the configuration, switched off without a secret, and refused at the start when they
// cannot work. The addresses are given here and not read from appsettings.Development.json, which is a developer's own file and is not
// in a clone.
[Collection(IntegrationTestCollection.Name)]
public class TurnStartupTests
{
    private readonly ChatApiFactory _factory;

    public TurnStartupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> WithSecret(string secret) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Turn:Secret"] = secret,
                ["Turn:Urls:0"] = "turn:localhost:3478?transport=udp",
                ["Turn:Urls:1"] = "turn:localhost:3478?transport=tcp",
                ["Turn:StunUrls:0"] = "stun:localhost:3478",
            })));

    [Fact]
    public void A_host_without_a_secret_hands_out_nothing()
    {
        var servers = _factory.Services.GetRequiredService<IceServers>();

        Assert.Empty(servers.For(Guid.NewGuid()));
    }

    [Fact]
    public void A_host_with_a_secret_hands_out_the_addresses_it_is_set_up_with()
    {
        using var host = WithSecret(new string('k', 40));

        var servers = host.Services.GetRequiredService<IceServers>().For(Guid.NewGuid());

        var turn = servers.Last();
        Assert.Contains("turn:localhost:3478?transport=udp", turn.Urls);
        Assert.NotNull(turn.Credential);
        Assert.Contains("stun:localhost:3478", servers.First().Urls);
    }

    [Fact]
    public void A_host_with_a_secret_too_short_to_be_one_does_not_start_and_says_what_is_wrong_without_the_secret()
    {
        using var host = WithSecret("too-short-to-be-a-secret");

        var error = Assert.ThrowsAny<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains("Turn:Secret must be at least", error.Message);
        Assert.DoesNotContain("too-short-to-be-a-secret", error.Message);
    }
}
