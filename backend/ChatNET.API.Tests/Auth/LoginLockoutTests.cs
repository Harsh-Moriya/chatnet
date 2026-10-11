using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Auth.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Auth;

// The rate limiter slows one client address down; lockout protects one account whoever the
// guesses come from. Five consecutive failures lock the account for 15 minutes.
[Collection(IntegrationTestCollection.Name)]
public class LoginLockoutTests
{
    private const int MaxFailedAttempts = 5;

    private readonly ChatApiFactory _factory;

    public LoginLockoutTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_password()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClient();

        await FailLoginsAsync(client, user, MaxFailedAttempts);

        var locked = await LoginAsync(client, user.Email, user.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("application/problem+json", locked.Content.Headers.ContentType?.MediaType);

        var problem = await locked.ReadProblemAsync();
        Assert.Equal(429, problem.Status);
        Assert.Contains("15 minutes", problem.Detail);

        var retryAfter = int.Parse(Assert.Single(locked.Headers.GetValues("Retry-After")));
        Assert.InRange(retryAfter, 1, 15 * 60);
    }

    [Fact]
    public async Task A_locked_account_stays_locked_for_further_attempts()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClient();
        await FailLoginsAsync(client, user, MaxFailedAttempts);

        var wrong = await LoginAsync(client, user.Email, user.Password + "x");
        var right = await LoginAsync(client, user.Email, user.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task The_attempt_that_triggers_the_lockout_is_still_an_ordinary_401()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClient();

        // The first four are ordinary failures, and so is the fifth: the response never says
        // "you are now locked" to someone who may only be guessing.
        for (var attempt = 1; attempt <= MaxFailedAttempts; attempt++)
        {
            var response = await LoginAsync(client, user.Email, user.Password + "x");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Invalid email or password.", (await response.ReadProblemAsync()).Detail);
        }
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClient();

        await FailLoginsAsync(client, user, MaxFailedAttempts - 1);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, user.Email, user.Password)).StatusCode);

        // Four more failures would be eight in a row without the reset, which would lock it.
        await FailLoginsAsync(client, user, MaxFailedAttempts - 1);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, user.Email, user.Password)).StatusCode);
    }

    [Fact]
    public async Task The_lockout_ends_when_its_time_is_up()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClient();
        await FailLoginsAsync(client, user, MaxFailedAttempts);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, user.Email, user.Password)).StatusCode);

        // Waiting 15 minutes is not practical in a test, so the lockout end is moved into the past.
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var entity = await users.FindByIdAsync(user.Id.ToString());
            await users.SetLockoutEndDateAsync(entity!, DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, user.Email, user.Password)).StatusCode);
    }

    [Fact]
    public async Task Locking_one_account_does_not_affect_another()
    {
        var victim = await _factory.RegisterUserAsync("victim");
        var other = await _factory.RegisterUserAsync("other");
        using var client = _factory.CreateClient();

        await FailLoginsAsync(client, victim, MaxFailedAttempts);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, victim.Email, victim.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, other.Email, other.Password)).StatusCode);
    }

    [Fact]
    public async Task Unknown_emails_get_the_same_401_and_nothing_to_lock()
    {
        using var client = _factory.CreateClient();
        var email = $"ghost_{Guid.NewGuid():N}@example.test";

        for (var attempt = 0; attempt < MaxFailedAttempts + 2; attempt++)
        {
            var response = await LoginAsync(client, email, "Wrong1password");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static async Task FailLoginsAsync(HttpClient client, TestUser user, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await LoginAsync(client, user.Email, user.Password + "x");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });
}
