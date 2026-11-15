using ChatNET.API.Calls;
using ChatNET.API.Calls.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Calls;

// A callee who has no live connection is alerted by push that somebody is calling, as a person who is on the page is rung by the page. The
// alert says who and what kind of call and nothing else, is replaced on the screen by the missed-call alert if the call is missed (they
// share a tag), is not sent for a call that is over, and respects a muted conversation.
[Collection(IntegrationTestCollection.Name)]
public class CallPushTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public CallPushTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.RemoveWaitingOutboxRowsAsync();
        await _factory.RemoveSubscriptionsAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.RemoveWaitingOutboxRowsAsync();
        await _factory.RemoveSubscriptionsAsync();
    }

    private (WebApplicationFactory<Program> Host, FakePushService Service) PushHost(Dictionary<string, string?>? quick = null)
    {
        var service = new FakePushService();
        void Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
            services.AddHttpClient<IPushSender, WebPushSender>().ConfigurePrimaryHttpMessageHandler(() => service));

        var host = quick is null ? _factory.WithPush(Configure) : _factory.QuickCallsWithPush(Configure, quick);
        return (host, service);
    }

    // The push is made in the background, so a test waits for it.
    private static async Task<IReadOnlyList<RecordedPush>> WaitForPushesAsync(FakePushService service, int count)
    {
        for (var i = 0; i < 100 && service.Received.Count < count; i++)
            await Task.Delay(50);

        return service.Received;
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid Conversation, Browser Browser)> AwayCalleeAsync()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(bob.Id, browser);
        return (alice, bob, conversation, browser);
    }

    [Fact]
    public async Task An_away_callee_is_alerted_that_somebody_is_calling_in_words_that_name_the_call_and_nothing_else()
    {
        var (alice, _, conversation, browser) = await AwayCalleeAsync();
        var (host, service) = PushHost();
        using var _ = host;
        await using var caller = await HubClient.ConnectAsync(host, alice);

        var callId = (await caller.StartCallAsync(conversation, "Video")).GetProperty("callId").GetGuid();

        var pushes = await WaitForPushesAsync(service, 1);
        var alert = Assert.Single(pushes).AlertFor(browser);
        Assert.Equal(alice.DisplayName, alert.GetProperty("title").GetString());
        Assert.Equal("Incoming video call", alert.GetProperty("body").GetString());
        Assert.Equal("call", alert.GetProperty("type").GetString());
        Assert.Equal($"call-{callId}", alert.GetProperty("tag").GetString());
        Assert.Equal(conversation, alert.GetProperty("conversationId").GetGuid());
        await caller.EndCallAsync(callId);
    }

    [Fact]
    public async Task A_callee_who_has_the_page_open_is_rung_by_the_page_and_not_alerted_by_push()
    {
        var (alice, bob, conversation, _) = await AwayCalleeAsync();
        var (host, service) = PushHost();
        using var _ = host;
        await using var caller = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);

        var callId = (await caller.StartCallAsync(conversation)).GetProperty("callId").GetGuid();

        await callee.WaitForIncomingCallAsync(callId);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Empty(service.Received);
        await caller.EndCallAsync(callId);
    }

    [Fact]
    public async Task A_callee_who_muted_the_conversation_is_not_alerted_by_push()
    {
        var (alice, bob, conversation, _) = await AwayCalleeAsync();
        using (var bobsClient = _factory.CreateClientFor(bob))
            await bobsClient.SetMutedAsync(conversation, true);
        var (host, service) = PushHost();
        using var _ = host;
        await using var caller = await HubClient.ConnectAsync(host, alice);

        var callId = (await caller.StartCallAsync(conversation)).GetProperty("callId").GetGuid();

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Empty(service.Received);
        await caller.EndCallAsync(callId);
    }

    [Fact]
    public async Task A_call_that_is_over_by_the_time_the_alert_is_made_is_not_alerted()
    {
        var (alice, bob, conversation, _) = await AwayCalleeAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        var callId = (await caller.StartCallAsync(conversation)).GetProperty("callId").GetGuid();
        await caller.EndCallAsync(callId);
        var (host, service) = PushHost();
        using var _ = host;

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PushAlerts>().AlertForCallAsync(
                new CallAlertRequested(callId, conversation, alice.Id, bob.Id, alice.DisplayName, CallMedia.Audio), CancellationToken.None);
        }

        Assert.Empty(service.Received);
    }

    [Fact]
    public async Task A_call_that_is_missed_has_its_alert_replaced_by_the_missed_call_alert_under_the_same_tag()
    {
        var (alice, _, conversation, browser) = await AwayCalleeAsync();
        var (host, service) = PushHost(new());
        using var _ = host;
        await using var caller = await HubClient.ConnectAsync(host, alice);

        var callId = (await caller.StartCallAsync(conversation)).GetProperty("callId").GetGuid();
        await caller.WaitForCallEndedAsync(callId, TimeSpan.FromSeconds(10));
        await host.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        var pushes = await WaitForPushesAsync(service, 2);
        var alerts = pushes.Select(p => p.AlertFor(browser)).ToList();
        Assert.Equal(["call", "missed-call"], alerts.Select(a => a.GetProperty("type").GetString()).Order(StringComparer.Ordinal));
        Assert.All(alerts, a => Assert.Equal($"call-{callId}", a.GetProperty("tag").GetString()));
        Assert.Equal("Missed audio call", alerts.Single(a => a.GetProperty("type").GetString() == "missed-call").GetProperty("body").GetString());
    }
}
