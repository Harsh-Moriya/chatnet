using System.Net;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Auth;

// A refresh token works once. Presenting one a second time means one of two things: two tabs refreshed
// at the same moment, or a copy of the token is in someone else's hands. The server tells them apart by
// how long ago the token was replaced, and treats the second case as theft.
[Collection(IntegrationTestCollection.Name)]
public class RefreshTokenReuseTests
{
    private static readonly TimeSpan JustOverTheGrace = TimeSpan.FromSeconds(31);

    private readonly ChatApiFactory _factory;

    public RefreshTokenReuseTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_token_presented_again_at_once_is_told_to_retry_and_nothing_else_changes()
    {
        var user = await _factory.RegisterUserAsync("race");
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = host.CreateCookielessClient();
        var first = await client.SignInAsync(user);
        var second = (await client.RefreshAsync(first)).GetRefreshCookie()!.Value.ToString();

        clock.Advance(TimeSpan.FromSeconds(5));
        var again = await client.RefreshAsync(first);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Null(again.GetRefreshCookie());
        var stillWorks = await client.RefreshAsync(second);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task A_replaced_token_presented_after_the_grace_period_revokes_the_whole_family()
    {
        var user = await _factory.RegisterUserAsync("theft");
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = host.CreateCookielessClient();
        var stolen = await client.SignInAsync(user);
        var current = (await client.RefreshAsync(stolen)).GetRefreshCookie()!.Value.ToString();

        clock.Advance(JustOverTheGrace);
        var thief = await client.RefreshAsync(stolen);

        Assert.Equal(HttpStatusCode.Unauthorized, thief.StatusCode);
        Assert.True(thief.DeletesRefreshCookie());

        // The owner's token is from the same family, so it stops working too: there is no telling which
        // of the two holders is the thief.
        var owner = await client.RefreshAsync(current);
        Assert.Equal(HttpStatusCode.Unauthorized, owner.StatusCode);

        Assert.All(await _factory.SessionRowsAsync(current), r => Assert.NotNull(r.RevokedAt));
    }

    [Fact]
    public async Task Theft_from_one_browser_leaves_the_users_other_sessions_alone()
    {
        var user = await _factory.RegisterUserAsync("others");
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = host.CreateCookielessClient();
        var laptop = await client.SignInAsync(user);
        var phone = await client.SignInAsync(user);
        await client.RefreshAsync(laptop);

        clock.Advance(JustOverTheGrace);
        await client.RefreshAsync(laptop);

        var response = await client.RefreshAsync(phone);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Detected_reuse_is_logged_with_ids_and_never_the_token()
    {
        var user = await _factory.RegisterUserAsync("logged");
        var clock = new ManualTimeProvider();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithClock(clock).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = host.CreateCookielessClient();
        var stolen = await client.SignInAsync(user);
        var current = (await client.RefreshAsync(stolen)).GetRefreshCookie()!.Value.ToString();

        clock.Advance(JustOverTheGrace);
        await client.RefreshAsync(stolen);

        var family = (await _factory.SessionRowsAsync(current)).First().FamilyId;
        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains("retired refresh token"));
        Assert.Contains(user.Id.ToString(), warning);
        Assert.Contains(family.ToString(), warning);
        Assert.DoesNotContain(capture.Entries, e => e.Contains(stolen) || e.Contains(current));
    }

    // The conditional update is what makes this safe: of any number of requests with the same token,
    // Postgres lets exactly one change the row. A check in code first would let several through.
    [Fact]
    public async Task Many_simultaneous_refreshes_of_one_token_give_exactly_one_success()
    {
        var user = await _factory.RegisterUserAsync("many");
        using var client = _factory.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.RefreshAsync(token)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var rows = await _factory.SessionRowsAsync(token);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.RevokedAt is null);
    }

    // Losing that race is not theft: the family must survive it.
    [Fact]
    public async Task The_winner_of_a_race_keeps_a_working_session()
    {
        var user = await _factory.RegisterUserAsync("winner");
        using var client = _factory.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.RefreshAsync(token)));

        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK).GetRefreshCookie()!.Value.ToString();
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(winner)).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_user_is_not_renewed_and_the_session_ends()
    {
        var user = await _factory.RegisterUserAsync("deactivated");
        using var client = _factory.CreateCookielessClient();
        var token = await client.SignInAsync(user);
        await _factory.ExecuteDbAsync(db => db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeactivated, true)));

        var response = await client.RefreshAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.DeletesRefreshCookie());
        Assert.All(await _factory.SessionRowsAsync(token), r => Assert.NotNull(r.RevokedAt));
    }
}
