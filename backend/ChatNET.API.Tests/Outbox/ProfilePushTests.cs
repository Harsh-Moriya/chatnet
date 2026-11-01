using System.Net;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Outbox;

// The pushes of a name or picture change, made by the relay when the request's own push failed. Two hosts share the database
// and Redis, as in MembershipPushTests: the requests go through one whose hub always fails, the relay runs on the other with a
// clock the test moves, and real people are connected to see what reaches them.
[Collection(IntegrationTestCollection.Name)]
public class ProfilePushTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public ProfilePushTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    public Task DisposeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    private WebApplicationFactory<Program> BrokenHub() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
        }));

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    private static Task RunAsync(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

    [Fact]
    public async Task A_name_change_whose_push_failed_reaches_the_contacts_and_the_persons_tabs_through_the_relay()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var name = $"Late {Guid.NewGuid():N}";

        var response = await client.UpdateProfileResponseAsync(name);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await bobTab.ReceivedProfileChangedAsync(alice.Id));
        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("userId", alice.Id), r => r.Kind == ProfileChanged.Name);
        Assert.Null(row.DispatchedAt);

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        Assert.Equal(name, (await bobTab.WaitForProfileChangedAsync(alice.Id)).GetProperty("displayName").GetString());
        Assert.Equal(name, (await aliceTab.WaitForProfileChangedAsync(alice.Id)).GetProperty("displayName").GetString());
        Assert.NotNull((await _factory.OutboxRowAsync(row.Id)).DispatchedAt);
    }

    [Fact]
    public async Task A_late_push_says_what_is_true_when_it_is_made_and_not_what_was_true_when_the_change_was()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var suffix = Guid.NewGuid().ToString("N");

        await client.UpdateProfileResponseAsync($"First {suffix}");
        await client.UpdateProfileResponseAsync($"Second {suffix}");

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        await bobTab.WaitForProfileChangedAsync(alice.Id);
        Assert.True(await bobTab.ReceivedProfileChangedAsync(alice.Id));
        Assert.All(
            bobTab.ProfileChanges.Where(p => p.GetProperty("id").GetGuid() == alice.Id),
            p => Assert.Equal($"Second {suffix}", p.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task A_picture_change_whose_push_failed_reaches_the_contacts_through_the_relay()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);

        var address = await client.SetAvatarAsync();
        Assert.False(await bobTab.ReceivedProfileChangedAsync(alice.Id));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        Assert.Equal(address, (await bobTab.WaitForProfileChangedAsync(alice.Id)).AvatarUrlOf());
    }
}
