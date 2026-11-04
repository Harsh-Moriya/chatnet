using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChatNET.API.Tests.Push;

// A browser keeps its registration alive by loading the page or by being pushed to. One that has done neither for 60 days (the
// default, Push:SubscriptionRetention) is removed, and one that is in use is never touched.
[Collection(IntegrationTestCollection.Name)]
public class PushSubscriptionCleanupTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public PushSubscriptionCleanupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // The cleanup removes by age across every person, so rows other tests left would be counted.
    public Task InitializeAsync() => _factory.RemoveSubscriptionsAsync();

    public Task DisposeAsync() => _factory.RemoveSubscriptionsAsync();

    private static Task CleanUpAsync(IServiceProvider services) => services
        .GetServices<IHostedService>()
        .OfType<PushSubscriptionCleanupService>()
        .Single()
        .CleanUpOnceAsync(CancellationToken.None);

    [Fact]
    public async Task Registrations_not_used_for_more_than_60_days_are_removed_and_the_rest_are_kept()
    {
        var alice = await _factory.RegisterUserAsync("cleanup");
        var longUnused = Browser.Subscribe();
        var almost = Browser.Subscribe();
        var recent = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, longUnused, lastUsedAt: DateTime.UtcNow.AddDays(-61));
        await _factory.AddSubscriptionAsync(alice.Id, almost, lastUsedAt: DateTime.UtcNow.AddDays(-59));
        await _factory.AddSubscriptionAsync(alice.Id, recent, lastUsedAt: DateTime.UtcNow);

        await CleanUpAsync(_factory.Services);

        var kept = (await _factory.SubscriptionsOfAsync(alice.Id)).Select(s => s.Endpoint).ToList();
        Assert.DoesNotContain(longUnused.Endpoint, kept);
        Assert.Contains(almost.Endpoint, kept);
        Assert.Contains(recent.Endpoint, kept);
    }

    [Fact]
    public async Task It_removes_by_age_for_everybody_and_changes_nothing_else()
    {
        var alice = await _factory.RegisterUserAsync("cleanup");
        var bob = await _factory.RegisterUserAsync("cleanup");
        var bobsOld = Browser.Subscribe();
        var alicesRecent = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(bob.Id, bobsOld, lastUsedAt: DateTime.UtcNow.AddDays(-90));
        await _factory.AddSubscriptionAsync(alice.Id, alicesRecent);
        var before = Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));

        await CleanUpAsync(_factory.Services);

        Assert.Empty(await _factory.SubscriptionsOfAsync(bob.Id));
        var after = Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.LastUsedAt, after.LastUsedAt);
    }

    [Fact]
    public async Task Running_it_again_with_nothing_to_remove_changes_nothing()
    {
        var alice = await _factory.RegisterUserAsync("again");
        await _factory.AddSubscriptionAsync(alice.Id, Browser.Subscribe());

        await CleanUpAsync(_factory.Services);
        await CleanUpAsync(_factory.Services);

        Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    [Fact]
    public async Task The_time_is_the_one_in_the_settings()
    {
        var alice = await _factory.RegisterUserAsync("short");
        var threeDays = Browser.Subscribe();
        var oneDay = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, threeDays, lastUsedAt: DateTime.UtcNow.AddDays(-3));
        await _factory.AddSubscriptionAsync(alice.Id, oneDay, lastUsedAt: DateTime.UtcNow.AddDays(-1));
        using var host = _factory.WithPush(settings: new Dictionary<string, string?> { ["Push:SubscriptionRetention"] = "2.00:00:00" });

        await CleanUpAsync(host.Services);

        var kept = (await _factory.SubscriptionsOfAsync(alice.Id)).Select(s => s.Endpoint).ToList();
        Assert.Equal([oneDay.Endpoint], kept);
    }

    // A browser that is pushed to is marked as used (see PushAlertTests), which is what keeps it from being removed here: the two
    // rules fit together.
    [Fact]
    public async Task A_registration_that_was_renewed_by_the_page_is_not_removed()
    {
        var alice = await _factory.RegisterUserAsync("renewed");
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, browser, lastUsedAt: DateTime.UtcNow.AddDays(-80));
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        // The page registers every time it loads.
        (await client.PutSubscriptionAsync(browser)).EnsureSuccessStatusCode();
        await CleanUpAsync(_factory.Services);

        Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
    }
}
