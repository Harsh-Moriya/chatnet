using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Auth.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Auth;

// Checking a password costs real time. If signing in with an email that does not exist skipped that
// work, the answer would come back measurably faster, and anyone could use the difference to find
// out which emails are registered. The login must therefore do one password check in both cases.
// Counting the checks is exact; timing them would be flaky.
[Collection(IntegrationTestCollection.Name)]
public class LoginTimingTests
{
    private readonly ChatApiFactory _factory;

    public LoginTimingTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task An_unknown_email_costs_the_same_password_check_as_a_wrong_password()
    {
        var user = await _factory.RegisterUserAsync("timing");
        var counter = new CountingHasher();
        using var host = WithHasher(counter);
        using var client = host.CreateClient();

        counter.Reset();
        var unknown = await client.PostAsJsonAsync("/api/auth/login",
            new { email = $"nobody_{Guid.NewGuid():N}@example.test", password = "Aa1wrongwrong" });
        var unknownChecks = counter.Verifications;

        counter.Reset();
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = user.Password + "x" });
        var wrongChecks = counter.Verifications;

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(1, wrongChecks);
        Assert.Equal(wrongChecks, unknownChecks);
    }

    [Fact]
    public async Task The_answer_for_an_unknown_email_is_identical_to_the_answer_for_a_wrong_password()
    {
        var user = await _factory.RegisterUserAsync("same");
        using var client = _factory.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/auth/login",
            new { email = $"nobody_{Guid.NewGuid():N}@example.test", password = "Aa1wrongwrong" });
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = user.Password + "x" });

        Assert.Equal((await unknown.ReadProblemAsync()).Detail, (await wrong.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_correct_login_still_works_with_the_counting_hasher_in_place()
    {
        var user = await _factory.RegisterUserAsync("works");
        using var host = WithHasher(new CountingHasher());
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = user.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithHasher(CountingHasher hasher) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<AppUser>>();
            services.AddSingleton<IPasswordHasher<AppUser>>(hasher);
        }));

    private sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();
        private int _verifications;

        public int Verifications => Volatile.Read(ref _verifications);

        public void Reset() => Interlocked.Exchange(ref _verifications, 0);

        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
