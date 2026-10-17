using ChatNET.API.Presence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Presence;

// The store is the part of presence that has to be right when several servers act at once, so these
// tests use the real Redis and real concurrency. Entries here live for one second, short enough to
// let some expire inside a test without a test taking long.
[Collection(IntegrationTestCollection.Name)]
public class PresenceStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(1);

    // A wait long enough that it is still running when a test checks, for tests that are not about its end.
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly ChatApiFactory _factory;

    public PresenceStoreTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private PresenceStore NewStore(TimeSpan? ttl = null)
    {
        var connectionString = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!;
        var options = new PresenceOptions
        {
            Ttl = ttl ?? Ttl,
            RefreshInterval = TimeSpan.FromMilliseconds(250),
            OfflineGrace = TimeSpan.Zero,
        };
        return new PresenceStore(new RedisConnection(connectionString), Options.Create(options));
    }

    private static Task WaitForExpiryAsync() => Task.Delay(Ttl + TimeSpan.FromMilliseconds(300));

    // ##### Coming online #####

    [Fact]
    public async Task The_first_connection_of_a_user_brings_them_online_and_a_second_does_not()
    {
        var store = NewStore();
        var user = Guid.NewGuid();

        var first = await store.TouchAsync(user, "tab-1", default);
        var second = await store.TouchAsync(user, "tab-2", default);

        Assert.True(first.CameOnline);
        Assert.False(second.CameOnline);
    }

    [Fact]
    public async Task Renewing_a_connection_does_not_bring_the_user_online_again()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);

        var again = await store.TouchAsync(user, "tab-1", default);

        Assert.False(again.CameOnline);
    }

    [Fact]
    public async Task Different_users_are_independent()
    {
        var store = NewStore();

        var one = await store.TouchAsync(Guid.NewGuid(), "tab-1", default);
        var other = await store.TouchAsync(Guid.NewGuid(), "tab-1", default);

        Assert.True(one.CameOnline);
        Assert.True(other.CameOnline);
    }

    [Fact]
    public async Task Many_connections_can_be_renewed_in_one_round_trip_and_answer_in_order()
    {
        var store = NewStore();
        var online = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        await store.TouchAsync(online, "tab-1", default);

        var results = await store.TouchManyAsync([(online, "tab-1"), (fresh, "tab-1"), (online, "tab-2")], default);

        Assert.Equal([false, true, false], results.Select(r => r.CameOnline));
    }

    [Fact]
    public async Task Renewing_no_connections_does_nothing()
    {
        Assert.Empty(await NewStore().TouchManyAsync([], default));
    }

    // ##### Going offline #####

    [Fact]
    public async Task Closing_one_of_two_connections_leaves_the_user_online()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);
        await store.TouchAsync(user, "tab-2", default);

        var closed = await store.CloseAsync(user, "tab-1", TimeSpan.Zero, default);

        Assert.Equal(SettleOutcome.StillOnline, closed.Outcome);
    }

    [Fact]
    public async Task Closing_the_last_connection_takes_the_user_offline_once()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);

        var first = await store.CloseAsync(user, "tab-1", TimeSpan.Zero, default);
        var again = await store.CloseAsync(user, "tab-1", TimeSpan.Zero, default);

        Assert.Equal(SettleOutcome.WentOffline, first.Outcome);
        Assert.Equal(SettleOutcome.AlreadyOffline, again.Outcome);
    }

    // The point of the Lua script: however many callers decide at once that a user is gone, exactly one
    // of them is told so and announces it.
    [Fact]
    public async Task When_many_callers_race_to_finish_one_user_exactly_one_wins()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);

        var results = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => Task.Run(() => store.CloseAsync(user, "tab-1", TimeSpan.Zero, default))));

        Assert.Equal(1, results.Count(r => r.Outcome == SettleOutcome.WentOffline));
        Assert.Equal(29, results.Count(r => r.Outcome == SettleOutcome.AlreadyOffline));
    }

    [Fact]
    public async Task The_same_race_through_two_separate_connections_to_redis_still_has_one_winner()
    {
        var one = NewStore();
        var two = NewStore();
        var user = Guid.NewGuid();
        await one.TouchAsync(user, "tab-1", default);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => (i % 2 == 0 ? one : two).CloseAsync(user, "tab-1", TimeSpan.Zero, default))));

        Assert.Equal(1, results.Count(r => r.Outcome == SettleOutcome.WentOffline));
    }

    // ##### The wait before offline #####

    [Fact]
    public async Task A_user_who_is_left_waiting_stays_online_until_the_wait_is_over()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        var grace = TimeSpan.FromMilliseconds(600);
        await store.TouchAsync(user, "tab-1", default);

        var closed = await store.CloseAsync(user, "tab-1", grace, default);
        var tooEarly = await store.FinishAsync(user, default);
        var duringWait = await store.GetSnapshotAsync([user], default);

        await Task.Delay(grace + TimeSpan.FromMilliseconds(200));
        var finished = await store.FinishAsync(user, default);
        var afterWait = await store.GetSnapshotAsync([user], default);

        Assert.Equal(SettleOutcome.NoConnectionsLeft, closed.Outcome);
        Assert.InRange(closed.Wait, TimeSpan.FromMilliseconds(1), grace);
        Assert.Equal(SettleOutcome.NoConnectionsLeft, tooEarly.Outcome);
        Assert.InRange(tooEarly.Wait, TimeSpan.FromMilliseconds(1), grace);
        Assert.Contains(user, duringWait.Online);
        Assert.Equal(SettleOutcome.WentOffline, finished.Outcome);
        Assert.DoesNotContain(user, afterWait.Online);
    }

    // The case the browser check found: two closes in quick succession leave two timers, and the first
    // one wakes while the second close's wait is still running. It must not take the user offline.
    [Fact]
    public async Task A_later_close_restarts_the_wait_so_an_earlier_timer_cannot_cut_it_short()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        var grace = TimeSpan.FromSeconds(1);
        await store.TouchAsync(user, "tab-1", default);

        var sinceFirstClose = System.Diagnostics.Stopwatch.StartNew();
        await store.CloseAsync(user, "tab-1", grace, default);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        await store.TouchAsync(user, "tab-2", default);
        await store.CloseAsync(user, "tab-2", grace, default);

        // Past the end of the first wait, inside the second.
        await Task.Delay(grace - sinceFirstClose.Elapsed + TimeSpan.FromMilliseconds(150));
        var firstTimer = await store.FinishAsync(user, default);
        var duringSecondWait = await store.GetSnapshotAsync([user], default);

        // Past the end of the second.
        await Task.Delay(firstTimer.Wait + TimeSpan.FromMilliseconds(200));
        var secondTimer = await store.FinishAsync(user, default);

        Assert.Equal(SettleOutcome.NoConnectionsLeft, firstTimer.Outcome);
        Assert.Contains(user, duringSecondWait.Online);
        Assert.Equal(SettleOutcome.WentOffline, secondTimer.Outcome);
    }

    [Fact]
    public async Task Only_one_of_several_timers_for_a_user_announces_them_offline()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        var grace = TimeSpan.FromMilliseconds(400);
        await store.TouchAsync(user, "tab-1", default);
        await store.CloseAsync(user, "tab-1", grace, default);
        await Task.Delay(grace + TimeSpan.FromMilliseconds(200));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.FinishAsync(user, default))));

        Assert.Single(results, r => r.Outcome == SettleOutcome.WentOffline);
        Assert.All(results.Where(r => r.Outcome != SettleOutcome.WentOffline), r => Assert.Equal(SettleOutcome.AlreadyOffline, r.Outcome));
    }

    // A user the sweeper finds (their entries expired) whose last connection closed a moment ago is
    // still in the wait; the timer that close started will finish them.
    [Fact]
    public async Task The_sweeper_leaves_a_user_who_is_in_a_running_wait()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);
        await WaitForExpiryAsync();
        await store.CloseAsync(user, "tab-1", Grace, default);

        var found = await store.GetExpiredAsync(1000, default);
        var swept = await store.FinishAsync(user, default);

        Assert.Contains(found.Users, u => u.UserId == user);
        Assert.Equal(SettleOutcome.NoConnectionsLeft, swept.Outcome);
    }

    // The page refresh: the old connection closes, a new one opens before the wait is over. Nobody
    // was told "offline", so nobody is told "online" either.
    [Fact]
    public async Task A_user_who_reconnects_while_pending_stays_online_without_a_new_announcement()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);
        await store.CloseAsync(user, "tab-1", Grace, default);

        var reconnected = await store.TouchAsync(user, "tab-2", default);
        var finished = await store.FinishAsync(user, default);

        Assert.False(reconnected.CameOnline);
        Assert.Equal(SettleOutcome.StillOnline, finished.Outcome);
    }

    [Fact]
    public async Task A_user_who_connects_after_going_offline_comes_online_again()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);
        await store.CloseAsync(user, "tab-1", TimeSpan.Zero, default);

        var back = await store.TouchAsync(user, "tab-2", default);

        Assert.True(back.CameOnline);
    }

    // ##### Entries that expire #####

    [Fact]
    public async Task An_entry_that_is_not_renewed_is_found_by_the_sweeper_after_it_expires_and_not_before()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);

        var before = await store.GetExpiredAsync(1000, default);
        await WaitForExpiryAsync();
        var after = await store.GetExpiredAsync(1000, default);

        Assert.DoesNotContain(before.Users, u => u.UserId == user);
        Assert.Contains(after.Users, u => u.UserId == user);
    }

    [Fact]
    public async Task A_swept_user_goes_offline_once_even_with_two_sweepers()
    {
        var one = NewStore();
        var two = NewStore();
        var user = Guid.NewGuid();
        await one.TouchAsync(user, "tab-1", default);
        await WaitForExpiryAsync();

        var results = await Task.WhenAll(
            Task.Run(() => one.FinishAsync(user, default)),
            Task.Run(() => two.FinishAsync(user, default)));

        Assert.Equal(1, results.Count(r => r.Outcome == SettleOutcome.WentOffline));
    }

    [Fact]
    public async Task The_expired_entry_reports_when_the_user_was_last_active()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        var touched = await store.TouchAsync(user, "tab-1", default);
        await WaitForExpiryAsync();

        var expired = (await store.GetExpiredAsync(1000, default)).Users.Single(u => u.UserId == user);

        Assert.InRange((expired.LastActiveAt - touched.At).TotalMilliseconds, -5, 5);
    }

    [Fact]
    public async Task A_renewed_entry_does_not_expire()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);

        await Task.Delay(600);
        await store.TouchAsync(user, "tab-1", default);
        await Task.Delay(600);
        var expired = await store.GetExpiredAsync(1000, default);

        // 1.2 seconds in, longer than the 1 second expiry, but renewed 0.6 seconds ago.
        Assert.DoesNotContain(expired.Users, u => u.UserId == user);
    }

    // The expiring score has to follow the newest connection that is left, or a user whose newer
    // connection closed would be treated as alive for as long as the closed one would have lasted.
    [Fact]
    public async Task After_the_newest_connection_closes_the_user_expires_with_the_one_that_is_left()
    {
        var store = NewStore();
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "older", default);
        await Task.Delay(500);
        await store.TouchAsync(user, "newer", default);
        await store.CloseAsync(user, "newer", TimeSpan.Zero, default);

        // The older entry expires 1 second after it was written, 0.5 seconds from now. Had the score
        // stayed at the newer entry's expiry the user would not be listed until 1 second from now.
        await Task.Delay(650);
        var expired = await store.GetExpiredAsync(1000, default);

        Assert.Contains(expired.Users, u => u.UserId == user);
    }

    [Fact]
    public async Task The_sweeper_never_returns_more_than_it_was_asked_for()
    {
        var store = NewStore();
        for (var i = 0; i < 5; i++)
            await store.TouchAsync(Guid.NewGuid(), "tab-1", default);
        await WaitForExpiryAsync();

        var expired = await store.GetExpiredAsync(3, default);

        Assert.True(expired.Users.Count <= 3);
    }

    [Fact]
    public async Task A_user_with_a_live_connection_who_is_checked_by_the_sweeper_stays_online()
    {
        var store = NewStore(TimeSpan.FromSeconds(30));
        var user = Guid.NewGuid();
        await store.TouchAsync(user, "tab-1", default);

        var checkedBySweeper = await store.FinishAsync(user, default);

        Assert.Equal(SettleOutcome.StillOnline, checkedBySweeper.Outcome);
    }

    // ##### Who is online #####

    [Fact]
    public async Task A_snapshot_lists_exactly_the_online_users_among_those_asked_about()
    {
        var store = NewStore(TimeSpan.FromSeconds(30));
        var online = Guid.NewGuid();
        var wentOffline = Guid.NewGuid();
        var neverSeen = Guid.NewGuid();
        await store.TouchAsync(online, "tab-1", default);
        await store.TouchAsync(wentOffline, "tab-1", default);
        await store.CloseAsync(wentOffline, "tab-1", TimeSpan.Zero, default);

        var snapshot = await store.GetSnapshotAsync([online, wentOffline, neverSeen], default);

        Assert.Equal([online], snapshot.Online);
    }

    [Fact]
    public async Task A_snapshot_of_nobody_is_empty_and_still_has_a_time()
    {
        var snapshot = await NewStore().GetSnapshotAsync([], default);

        Assert.Empty(snapshot.Online);
        Assert.InRange((DateTime.UtcNow - snapshot.At).TotalSeconds, -5, 5);
    }

    // ##### Order #####

    // Events are ordered by these times, so a later step must never have an earlier time.
    [Fact]
    public async Task Times_never_go_backwards_across_steps_in_the_order_they_ran()
    {
        var store = NewStore(TimeSpan.FromSeconds(30));
        var user = Guid.NewGuid();

        var touched = await store.TouchAsync(user, "tab-1", default);
        var settled = await store.CloseAsync(user, "tab-1", TimeSpan.Zero, default);
        var touchedAgain = await store.TouchAsync(user, "tab-2", default);
        var snapshot = await store.GetSnapshotAsync([user], default);

        Assert.True(touched.At <= settled.At);
        Assert.True(settled.At <= touchedAgain.At);
        Assert.True(touchedAgain.At <= snapshot.At);
    }
}
