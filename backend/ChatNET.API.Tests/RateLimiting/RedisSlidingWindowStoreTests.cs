using ChatNET.API.Presence;
using ChatNET.API.RateLimiting;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ChatNET.API.Tests.RateLimiting;

// The store is the part of rate limiting that has to be right when several servers act at once, so these tests use the real Redis
// and real concurrency. Windows here are a second or two, short enough to see requests leave them inside a test.
[Collection(IntegrationTestCollection.Name)]
public class RedisSlidingWindowStoreTests
{
    private readonly ChatApiFactory _factory;

    public RedisSlidingWindowStoreTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private RedisConnection NewConnection() =>
        new(_factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!);

    private RedisSlidingWindowStore NewStore() => new(NewConnection());

    private static string NewKey() => $"test:{Guid.NewGuid():N}";

    private static readonly CancellationToken Ct = CancellationToken.None;

    // ##### The limit #####

    [Fact]
    public async Task Requests_up_to_the_limit_go_ahead_and_the_next_one_is_refused_with_the_wait()
    {
        var store = NewStore();
        var key = NewKey();
        var window = TimeSpan.FromSeconds(10);

        for (var i = 0; i < 3; i++)
            Assert.True((await store.TryAcquireAsync(key, 3, window, Ct)).Allowed);

        var refused = await store.TryAcquireAsync(key, 3, window, Ct);

        Assert.False(refused.Allowed);
        Assert.False(refused.StoreUnavailable);
        // The oldest request leaves the window one window after it was made, a moment ago.
        Assert.InRange(refused.RetryAfter, TimeSpan.FromSeconds(8), window);
    }

    [Fact]
    public async Task A_limit_of_one_lets_the_first_through_and_refuses_the_second()
    {
        var store = NewStore();
        var key = NewKey();

        Assert.True((await store.TryAcquireAsync(key, 1, TimeSpan.FromSeconds(10), Ct)).Allowed);
        Assert.False((await store.TryAcquireAsync(key, 1, TimeSpan.FromSeconds(10), Ct)).Allowed);
    }

    [Fact]
    public async Task Keys_do_not_share_a_budget()
    {
        var store = NewStore();
        var abuser = NewKey();
        var bystander = NewKey();

        await store.TryAcquireAsync(abuser, 1, TimeSpan.FromSeconds(10), Ct);
        Assert.False((await store.TryAcquireAsync(abuser, 1, TimeSpan.FromSeconds(10), Ct)).Allowed);

        Assert.True((await store.TryAcquireAsync(bystander, 1, TimeSpan.FromSeconds(10), Ct)).Allowed);
    }

    // ##### The window #####

    // Not a fixed window: the second request of a pair that is a second apart leaves one second after the first, and
    // frees one place, not all of them.
    [Fact]
    public async Task The_window_slides_so_requests_leave_it_one_at_a_time()
    {
        var store = NewStore();
        var key = NewKey();
        var window = TimeSpan.FromSeconds(2);

        Assert.True((await store.TryAcquireAsync(key, 2, window, Ct)).Allowed);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True((await store.TryAcquireAsync(key, 2, window, Ct)).Allowed);
        Assert.False((await store.TryAcquireAsync(key, 2, window, Ct)).Allowed);

        // The first has left the window by now and the second has not.
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        Assert.True((await store.TryAcquireAsync(key, 2, window, Ct)).Allowed);

        var refused = await store.TryAcquireAsync(key, 2, window, Ct);
        Assert.False(refused.Allowed);
        Assert.InRange(refused.RetryAfter, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_refused_request_is_not_counted_so_asking_again_does_not_extend_the_wait()
    {
        var store = NewStore();
        var key = NewKey();
        var window = TimeSpan.FromSeconds(1);

        await store.TryAcquireAsync(key, 1, window, Ct);
        for (var i = 0; i < 20; i++)
            Assert.False((await store.TryAcquireAsync(key, 1, window, Ct)).Allowed);

        await Task.Delay(TimeSpan.FromSeconds(1.2));

        Assert.True((await store.TryAcquireAsync(key, 1, window, Ct)).Allowed);
    }

    [Fact]
    public async Task The_key_leaves_redis_when_the_window_has_passed()
    {
        var connection = NewConnection();
        var store = new RedisSlidingWindowStore(connection);
        var key = NewKey();

        await store.TryAcquireAsync(key, 5, TimeSpan.FromSeconds(1), Ct);
        var db = await connection.GetDatabaseAsync();

        var timeToLive = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await Task.Delay(TimeSpan.FromSeconds(1.3));
        Assert.False(await db.KeyExistsAsync(key));
    }

    // ##### Several at once #####

    // The count and the decision are one step in Redis. With a read and a write from the server, a hundred requests that
    // arrive together would all read "9" and all go ahead.
    [Fact]
    public async Task A_hundred_requests_at_once_get_exactly_the_limit()
    {
        var store = NewStore();
        var key = NewKey();

        var decisions = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => store.TryAcquireAsync(key, 10, TimeSpan.FromSeconds(30), Ct))));

        Assert.Equal(10, decisions.Count(d => d.Allowed));
    }

    // Two servers of the application each have a connection of their own, and one budget.
    [Fact]
    public async Task Two_servers_share_one_budget()
    {
        var first = NewStore();
        var second = NewStore();
        var key = NewKey();

        var decisions = await Task.WhenAll(Enumerable.Range(0, 60)
            .Select(i => Task.Run(() => (i % 2 == 0 ? first : second).TryAcquireAsync(key, 10, TimeSpan.FromSeconds(30), Ct))));

        Assert.Equal(10, decisions.Count(d => d.Allowed));
    }
}
