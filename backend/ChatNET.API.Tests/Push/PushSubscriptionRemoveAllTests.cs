using ChatNET.API.Push.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Push;

// A password reset removes every browser the person registered for alerts, because an alert carries the start of a message.
[Collection(IntegrationTestCollection.Name)]
public class PushSubscriptionRemoveAllTests
{
    private readonly ChatApiFactory _factory;

    public PushSubscriptionRemoveAllTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Every_browser_of_the_person_is_removed_and_nobody_elses()
    {
        var user = await _factory.RegisterUserAsync("pushall");
        var other = await _factory.RegisterUserAsync("pushallother");
        await _factory.AddSubscriptionAsync(user.Id, Browser.Subscribe());
        await _factory.AddSubscriptionAsync(user.Id, Browser.Subscribe());
        var kept = await _factory.AddSubscriptionAsync(other.Id, Browser.Subscribe());

        using var scope = _factory.Services.CreateScope();
        var removed = await scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>()
            .RemoveAllForUserAsync(user.Id, CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Empty(await _factory.SubscriptionsOfAsync(user.Id));
        Assert.Equal(kept.Id, Assert.Single(await _factory.SubscriptionsOfAsync(other.Id)).Id);
    }

    [Fact]
    public async Task A_person_with_no_browsers_loses_nothing()
    {
        var user = await _factory.RegisterUserAsync("pushnone");

        using var scope = _factory.Services.CreateScope();
        var removed = await scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>()
            .RemoveAllForUserAsync(user.Id, CancellationToken.None);

        Assert.Equal(0, removed);
    }
}
