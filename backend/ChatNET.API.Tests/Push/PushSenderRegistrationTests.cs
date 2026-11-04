using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Push;

// How the sender is wired into the host: the client it sends through is the one that gives up after the configured time and does
// not follow a redirect. A redirect from a push service would send the server to an address that was never checked against the
// list of allowed hosts.
[Collection(IntegrationTestCollection.Name)]
public class PushSenderRegistrationTests
{
    private readonly ChatApiFactory _factory;

    public PushSenderRegistrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static readonly string ClientName = nameof(IPushSender);

    [Fact]
    public void The_sender_is_the_one_that_talks_to_the_web_push_library()
    {
        using var host = _factory.WithPush();

        Assert.IsType<WebPushSender>(host.Services.GetRequiredService<IPushSender>());
    }

    [Fact]
    public void A_host_without_keys_can_still_build_the_sender()
    {
        Assert.IsType<WebPushSender>(_factory.Services.GetRequiredService<IPushSender>());
    }

    // A mistake in the keys shows when the host starts, not on the first push of a deployment that has been up for a day.
    [Fact]
    public void The_host_does_not_start_when_only_some_of_the_keys_are_set()
    {
        using var host = _factory.WithPush(settings: new Dictionary<string, string?> { ["Push:Subject"] = string.Empty });

        var failure = Record.Exception(() => host.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("must all be set, or none of them", AllMessages(failure));
    }

    [Fact]
    public void The_host_does_not_start_when_the_public_key_belongs_to_another_private_key()
    {
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = other.ExportParameters(false).Q;
        var publicKey = System.Buffers.Text.Base64Url.EncodeToString([0x04, .. q.X!, .. q.Y!]);
        using var host = _factory.WithPush(settings: new Dictionary<string, string?> { ["Push:PublicKey"] = publicKey });

        var failure = Record.Exception(() => host.CreateClient());

        Assert.NotNull(failure);
        var messages = AllMessages(failure);
        Assert.Contains("public half of the private key", messages);

        // The private key is a secret and a failure to start is logged.
        Assert.DoesNotContain(PushApi.Vapid.PrivateKey, messages);
    }

    private static string AllMessages(Exception failure)
    {
        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
            messages.Add(current.Message);

        return string.Join(" | ", messages);
    }

    [Fact]
    public void The_client_gives_up_after_the_configured_time()
    {
        using var host = _factory.WithPush(settings: new Dictionary<string, string?> { ["Push:RequestTimeout"] = "00:00:03" });

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        Assert.Equal(TimeSpan.FromSeconds(3), client.Timeout);
    }

    [Fact]
    public void The_client_does_not_follow_redirects_and_renews_its_connections()
    {
        using var host = _factory.WithPush();
        var options = host.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(ClientName);
        var builder = host.Services.GetRequiredService<HttpMessageHandlerBuilder>();
        builder.Name = ClientName;

        foreach (var action in options.HttpMessageHandlerBuilderActions)
            action(builder);

        var handler = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
    }
}
