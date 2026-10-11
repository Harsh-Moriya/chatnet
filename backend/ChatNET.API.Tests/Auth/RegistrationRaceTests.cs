using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Auth;

// Registration checks "is this email or user name taken?" and then inserts. Two requests that
// arrive together can both pass the check. Identity's user name index is unique, so one of them
// fails on insert, but its email index is not, so both could succeed and leave two accounts with
// one email address (after which signing in by that email breaks). These tests send many
// registrations at once and require exactly one winner.
[Collection(IntegrationTestCollection.Name)]
public class RegistrationRaceTests
{
    private const int Concurrent = 12;

    private readonly ChatApiFactory _factory;

    public RegistrationRaceTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Many_registrations_with_the_same_email_create_exactly_one_account()
    {
        var email = $"race_{Guid.NewGuid():N}@example.test";

        var statuses = await RegisterManyAsync(i => (UserName: Unique($"mail{i}"), Email: email));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.All(statuses.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.Users.CountAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task The_winner_can_still_sign_in_by_that_email()
    {
        var email = $"race_{Guid.NewGuid():N}@example.test";
        var statuses = await RegisterManyAsync(i => (UserName: Unique($"login{i}"), Email: email));
        Assert.Contains(HttpStatusCode.OK, statuses);
        using var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Many_registrations_with_the_same_user_name_create_exactly_one_account_and_never_fail_with_500()
    {
        var userName = Unique("name");

        var statuses = await RegisterManyAsync(i => (UserName: userName, Email: $"u{i}_{Guid.NewGuid():N}@example.test"));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.All(statuses.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.Users.CountAsync(u => u.UserName == userName)));
    }

    [Fact]
    public async Task Different_people_registering_at_the_same_time_do_not_block_each_other()
    {
        var statuses = await RegisterManyAsync(i => (UserName: Unique($"p{i}"), Email: $"p{i}_{Guid.NewGuid():N}@example.test"));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    private const string Password = "Aa1xxxxxxxxx";

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<List<HttpStatusCode>> RegisterManyAsync(Func<int, (string UserName, string Email)> identity)
    {
        var tasks = Enumerable.Range(0, Concurrent).Select(async i =>
        {
            var (userName, email) = identity(i);
            using var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/register",
                new { userName, displayName = "Racer", email, password = Password });
            return response.StatusCode;
        });

        return (await Task.WhenAll(tasks)).ToList();
    }
}
