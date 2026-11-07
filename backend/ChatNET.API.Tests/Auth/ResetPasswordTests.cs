using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Auth.Models;
using ChatNET.API.Push.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Auth;

// Using a reset link. The password changes, every session of the person ends, and the link, which is the only proof the person
// holds, is good for one use. Anything wrong with the link is one answer, so a guesser learns nothing from the difference.
[Collection(IntegrationTestCollection.Name)]
public class ResetPasswordTests
{
    private const string NewPassword = "Brand1NewPassword";

    private readonly ChatApiFactory _factory;

    public ResetPasswordTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, string token, string newPassword = NewPassword) =>
        client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword });

    private static async Task AssertInvalidLinkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("This link is invalid or has expired.", problem.Detail);
        Assert.True(problem.Errors!.Value.TryGetProperty("Token", out _));
    }

    private Task<List<RefreshToken>> SessionsOfAsync(Guid userId) =>
        _factory.QueryDbAsync(db => db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync());

    private async Task<int> UsableSessionsAsync(Guid userId) =>
        (await SessionsOfAsync(userId)).Count(t => t.RevokedAt is null);

    private WebApplicationFactory<Program> HostWith(Dictionary<string, string?> settings) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    // ##### What a reset does #####

    [Fact]
    public async Task The_new_password_works_the_old_one_does_not_and_no_session_is_issued()
    {
        var user = await _factory.RegisterUserAsync("resetok");
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateCookielessClient();

        var response = await ResetAsync(client, token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync(user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(user with { Password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task Every_session_of_the_person_ends_in_every_family_and_nobody_elses_do()
    {
        var user = await _factory.RegisterUserAsync("resetsessions");
        var other = await _factory.RegisterUserAsync("resetothers");
        using var client = _factory.CreateCookielessClient();
        var firstBrowser = await client.SignInAsync(user);
        var secondBrowser = await client.SignInAsync(user);
        var othersBrowser = await client.SignInAsync(other);
        var token = await _factory.AddResetTokenAsync(user.Id);

        await ResetAsync(client, token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync(firstBrowser)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync(secondBrowser)).StatusCode);
        Assert.Equal(0, await UsableSessionsAsync(user.Id));
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(othersBrowser)).StatusCode);
    }

    [Fact]
    public async Task The_other_links_that_were_waiting_are_voided_and_a_used_link_is_not_used_again()
    {
        var user = await _factory.RegisterUserAsync("resetvoid");
        var used = await _factory.AddResetTokenAsync(user.Id);
        var waiting = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, used)).StatusCode);

        await AssertInvalidLinkAsync(await ResetAsync(client, waiting, "Another1Password"));
        await AssertInvalidLinkAsync(await ResetAsync(client, used, "Another1Password"));
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(user with { Password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task The_security_stamp_changes_and_the_lockout_is_cleared()
    {
        var user = await _factory.RegisterUserAsync("resetlock");
        using var client = _factory.CreateClient();
        for (var attempt = 0; attempt < 5; attempt++)
            await client.LoginAsync(user with { Password = "Wrong1Password" });
        var locked = await _factory.QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id));
        Assert.NotNull(locked.LockoutEnd);
        var token = await _factory.AddResetTokenAsync(user.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token)).StatusCode);

        var after = await _factory.QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id));
        Assert.Null(after.LockoutEnd);
        Assert.Equal(0, after.AccessFailedCount);
        Assert.NotEqual(locked.SecurityStamp, after.SecurityStamp);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(user with { Password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task The_push_registrations_of_the_person_are_removed_and_nobody_elses()
    {
        var user = await _factory.RegisterUserAsync("resetpush");
        var other = await _factory.RegisterUserAsync("resetpushother");
        await _factory.AddSubscriptionAsync(user.Id, Browser.Subscribe());
        await _factory.AddSubscriptionAsync(user.Id, Browser.Subscribe());
        await _factory.AddSubscriptionAsync(other.Id, Browser.Subscribe());
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateClient();

        await ResetAsync(client, token);

        Assert.Empty(await _factory.SubscriptionsOfAsync(user.Id));
        Assert.Single(await _factory.SubscriptionsOfAsync(other.Id));
    }

    // Resetting needs no mail relay: a link that was sent before email was switched off still works.
    [Fact]
    public async Task A_link_still_works_while_email_is_off()
    {
        var user = await _factory.RegisterUserAsync("resetnomail");
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var host = HostWith(new() { ["Email:Smtp:Host"] = string.Empty });
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token)).StatusCode);
    }

    // ##### A link that does not work #####

    [Fact]
    public async Task An_unknown_a_used_and_an_expired_link_get_the_same_answer()
    {
        var user = await _factory.RegisterUserAsync("resetinvalid");
        var used = await _factory.AddResetTokenAsync(user.Id, usedAt: DateTime.UtcNow.AddMinutes(-5));
        var expired = await _factory.AddResetTokenAsync(user.Id, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var unknown = new string('a', 43);
        using var client = _factory.CreateClient();

        var answers = new[] { await ResetAsync(client, unknown), await ResetAsync(client, used), await ResetAsync(client, expired) };

        foreach (var answer in answers)
            await AssertInvalidLinkAsync(answer);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(user)).StatusCode);
    }

    [Fact]
    public async Task A_link_works_until_thirty_minutes_after_it_was_made_and_not_at_that_moment()
    {
        var user = await _factory.RegisterUserAsync("resetclock");
        var clock = new ManualTimeProvider();
        var expiresAt = clock.GetUtcNow().UtcDateTime.AddMinutes(30);
        var justBefore = await _factory.AddResetTokenAsync(user.Id, expiresAt);
        using var host = _factory.WithClock(clock);
        using var client = host.CreateClient();

        clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, justBefore)).StatusCode);

        // That reset voided every other link, so a new one is made for the moment of expiry itself.
        var atTheMoment = await _factory.AddResetTokenAsync(user.Id, expiresAt);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertInvalidLinkAsync(await ResetAsync(client, atTheMoment, "Another1Password"));
    }

    [Fact]
    public async Task A_deactivated_account_gets_the_same_answer_and_keeps_its_password()
    {
        var user = await _factory.RegisterUserAsync("resetdeact");
        await _factory.DeactivateAsync(user.Id);
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateClient();

        await AssertInvalidLinkAsync(await ResetAsync(client, token));

        // Rolled back with the rest, so nothing about the link changed either.
        Assert.Null((await _factory.ResetTokenAsync(token))!.UsedAt);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.LoginAsync(user)).StatusCode);
    }

    // ##### A new password that is not acceptable #####

    [Theory]
    [InlineData("alllowercase1")]
    [InlineData("ALLUPPERCASE1")]
    [InlineData("NoDigitsHere")]
    public async Task A_password_that_breaks_an_account_rule_is_a_400_on_the_field_and_the_link_still_works(string weak)
    {
        var user = await _factory.RegisterUserAsync("resetweak");
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateCookielessClient();
        var browser = await client.SignInAsync(user);
        await _factory.AddSubscriptionAsync(user.Id, Browser.Subscribe());

        var rejected = await ResetAsync(client, token, weak);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.ReadProblemAsync();
        Assert.True(problem.Errors!.Value.TryGetProperty("NewPassword", out _));

        // Nothing changed: the old password, the sessions and the registrations are as they were, and the link is unused.
        Assert.Null((await _factory.ResetTokenAsync(token))!.UsedAt);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(browser)).StatusCode);
        Assert.Single(await _factory.SubscriptionsOfAsync(user.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token)).StatusCode);
    }

    [Theory]
    [InlineData("", "Brand1NewPassword", "Token")]
    [InlineData("tooshort", "Brand1NewPassword", "Token")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+", "Brand1NewPassword", "Token")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "", "NewPassword")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Short1a", "NewPassword")]
    public async Task A_request_that_is_not_shaped_right_is_a_400_on_the_field_without_touching_the_database(string token, string password, string field)
    {
        using var client = _factory.CreateClient();

        var response = await ResetAsync(client, token, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync()).Errors!.Value.TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_password_longer_than_128_characters_is_refused()
    {
        var user = await _factory.RegisterUserAsync("resetlong");
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateClient();

        var response = await ResetAsync(client, token, "Aa1" + new string('x', 126));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await _factory.ResetTokenAsync(token))!.UsedAt);
    }

    // ##### Concurrency #####

    [Fact]
    public async Task Of_many_requests_with_the_same_link_at_the_same_moment_exactly_one_resets_the_password()
    {
        var user = await _factory.RegisterUserAsync("resetrace");
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateClient();
        var passwords = Enumerable.Range(0, 10).Select(i => $"Raced{i}Password").ToList();

        var answers = await Task.WhenAll(passwords.Select(password => Task.Run(() => ResetAsync(client, token, password))));

        Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(9, answers.Count(a => a.StatusCode == HttpStatusCode.BadRequest));

        // The password is the winner's and only the winner's.
        var winner = passwords[Array.FindIndex(answers, a => a.StatusCode == HttpStatusCode.NoContent)];
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(user with { Password = winner })).StatusCode);
        // A few of the others, not all: five wrong sign-ins in a row lock the account.
        foreach (var loser in passwords.Where(p => p != winner).Take(3))
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync(user with { Password = loser })).StatusCode);
    }

    // A renewal or a sign-in that is being made at this moment holds a share lock on the person. The reset takes the row for update,
    // so it waits for them, and then revokes what they made; without that wait a session could be stored after the revocation.
    [Fact]
    public async Task A_reset_waits_for_a_renewal_in_progress_and_then_revokes_the_session_it_made()
    {
        var user = await _factory.RegisterUserAsync("resetwaits");
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var client = _factory.CreateClient();
        await using var renewal = await _factory.HoldUserRowAsync(user.Id, shared: true);

        var reset = Task.Run(() => ResetAsync(client, token));
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Assert.False(reset.IsCompleted, "The reset went ahead while a renewal held the person's row.");

        await renewal.ExecuteAsync(
            """
            INSERT INTO "RefreshTokens" ("Id", "UserId", "FamilyId", "TokenHash", "CreatedAt", "ExpiresAt")
            VALUES (@id, @user, @family, @hash, now(), now() + interval '14 days')
            """,
            ("id", Guid.NewGuid()), ("user", user.Id), ("family", Guid.NewGuid()), ("hash", Guid.NewGuid().ToString("N")));
        await renewal.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await reset).StatusCode);
        Assert.Equal(0, await UsableSessionsAsync(user.Id));
    }

    // A renewal and a reset at the same moment: whichever order they run in, no session of the person is usable afterwards. The
    // locking that guarantees it is pinned deterministically in SessionGuardTests; this runs the real requests many times.
    [Fact]
    public async Task A_renewal_racing_a_reset_never_leaves_a_usable_session()
    {
        using var client = _factory.CreateCookielessClient();

        for (var round = 0; round < 12; round++)
        {
            var user = await _factory.RegisterUserAsync("resetvsrefresh");
            var browser = await client.SignInAsync(user);
            var token = await _factory.AddResetTokenAsync(user.Id);

            await Task.WhenAll(Task.Run(() => ResetAsync(client, token)), Task.Run(() => client.RefreshAsync(browser)));

            Assert.Equal(0, await UsableSessionsAsync(user.Id));
        }
    }

    // A sign-in that checked the old password and a reset at the same moment: either the sign-in came first and the reset ended its
    // session, or it came after and is refused.
    [Fact]
    public async Task A_sign_in_racing_a_reset_never_leaves_a_usable_session()
    {
        using var client = _factory.CreateCookielessClient();

        for (var round = 0; round < 12; round++)
        {
            var user = await _factory.RegisterUserAsync("resetvssignin");
            var token = await _factory.AddResetTokenAsync(user.Id);

            await Task.WhenAll(Task.Run(() => ResetAsync(client, token)), Task.Run(() => client.LoginAsync(user)));

            Assert.Equal(0, await UsableSessionsAsync(user.Id));
        }
    }

    // ##### All or nothing #####

    [Fact]
    public async Task A_failure_part_way_through_leaves_the_link_the_password_and_the_sessions_as_they_were()
    {
        var user = await _factory.RegisterUserAsync("resetatomic");
        var token = await _factory.AddResetTokenAsync(user.Id);
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPushSubscriptionRepository>();
            services.AddScoped<IPushSubscriptionRepository, FailingPushSubscriptions>();
        }));
        using var client = host.CreateCookielessClient();
        var browser = await client.SignInAsync(user);

        var response = await ResetAsync(client, token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null((await _factory.ResetTokenAsync(token))!.UsedAt);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(browser)).StatusCode);
    }

    private sealed class FailingPushSubscriptions : IPushSubscriptionRepository
    {
        public Task<int> RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database is away.");

        public Task SaveAsync(Guid userId, string endpoint, string p256dh, string auth, int maxPerUser, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(Guid userId, string endpoint, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<List<PushTarget>> GetForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkUsedAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RemoveUnusedSinceAsync(DateTime cutoff, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<List<Guid>> FindMessageRecipientsAsync(Guid conversationId, Guid senderId, IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // ##### Logs #####

    [Fact]
    public async Task Nothing_in_the_logs_holds_the_token_or_the_password_and_the_person_is_named_by_id()
    {
        var user = await _factory.RegisterUserAsync("resetlogs");
        var token = await _factory.AddResetTokenAsync(user.Id);
        var logs = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        using var client = host.CreateClient();

        await ResetAsync(client, "z".PadLeft(43, 'z'), "Never1Logged");
        await ResetAsync(client, token, NewPassword);

        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(token));
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(NewPassword) || entry.Contains("Never1Logged"));
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(user.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs.Entries, entry => entry.Contains($"Password reset for user {user.Id}"));
    }
}
