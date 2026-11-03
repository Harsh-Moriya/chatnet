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
