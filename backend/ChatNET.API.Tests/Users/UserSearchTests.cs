using System.Net;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Users;

// All tests share one database, so each builds its users around a unique tag. A search for the
// tag can then only ever match the users that test created.
[Collection(IntegrationTestCollection.Name)]
public class UserSearchTests
{
    private readonly ChatApiFactory _factory;

    public UserSearchTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Finds_users_whose_user_name_starts_with_the_term()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        await _factory.RegisterUserAsync($"{tag}_anna", "Anna Plain");
        await _factory.RegisterUserAsync($"{tag}_bert", "Bert Plain");
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync($"{tag}_an");

        var match = Assert.Single(results);
        Assert.Equal($"{tag}_anna", match.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task Finds_users_by_display_name_ignoring_case()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        var target = await _factory.RegisterUserAsync($"{tag}_dn", $"Zed {tag} Quartz");
        var other = await _factory.RegisterUserAsync($"{tag}_other", "Someone Else");
        using var client = _factory.CreateClientFor(caller);

        // The display name starts with "Zed"; the term is lower case and includes the tag, so
        // only this user can match.
        var results = await client.SearchUsersAsync($"zed {tag.ToLowerInvariant()}");

        var match = Assert.Single(results);
        Assert.Equal(target.Id, match.GetProperty("id").GetGuid());
        Assert.DoesNotContain(results, r => r.GetProperty("id").GetGuid() == other.Id);
    }

    [Fact]
    public async Task Matches_only_the_start_of_a_name_not_the_middle()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        await _factory.RegisterUserAsync($"x_{tag}", "Middle Match");
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync(tag);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Never_returns_the_caller()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync($"{tag}_me", $"{tag} Me");
        var colleague = await _factory.RegisterUserAsync($"{tag}_you", $"{tag} You");
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync(tag);

        var match = Assert.Single(results);
        Assert.Equal(colleague.Id, match.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Does_not_return_deactivated_users()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        var active = await _factory.RegisterUserAsync($"{tag}_on", $"{tag} Active");
        var gone = await _factory.RegisterUserAsync($"{tag}_off", $"{tag} Gone");
        await _factory.DeactivateAsync(gone);
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync(tag);

        var match = Assert.Single(results);
        Assert.Equal(active.Id, match.GetProperty("id").GetGuid());
    }

    // In SQL LIKE, "_" matches any single character and "%" matches anything. A user who types
    // them must get a literal search, otherwise "%%" would list everybody.
    [Fact]
    public async Task Underscore_in_the_term_is_literal_not_a_wildcard()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        await _factory.RegisterUserAsync($"{tag}_a", "Underscore Match");
        await _factory.RegisterUserAsync($"{tag}xa", "Would Match If Wildcard");
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync($"{tag}_");

        var match = Assert.Single(results);
        Assert.Equal($"{tag}_a", match.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task Percent_in_the_term_is_literal_not_a_wildcard()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        await _factory.RegisterUserAsync($"{tag}_p1", $"{tag} one");
        await _factory.RegisterUserAsync($"{tag}_p2", $"{tag} two");
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync($"{tag}%");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Returns_at_most_twenty_results_in_display_name_order()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        for (var i = 1; i <= 21; i++)
            await _factory.RegisterUserAsync($"{tag}_u{i:00}", $"{tag} {i:00}");
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync(tag);

        Assert.Equal(20, results.Count);
        var names = results.Select(r => r.GetProperty("displayName").GetString()!).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
        Assert.Equal($"{tag} 01", names[0]);
    }

    [Fact]
    public async Task Exposes_only_public_profile_fields()
    {
        var tag = Tag();
        var caller = await _factory.RegisterUserAsync();
        await _factory.RegisterUserAsync($"{tag}_pub", $"{tag} Public");
        using var client = _factory.CreateClientFor(caller);

        var results = await client.SearchUsersAsync(tag);

        // Anything else (email, password hash, lockout state) must never be added by accident.
        var fields = Assert.Single(results).EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "avatarUrl", "displayName", "id", "userName" }, fields);
    }

    [Fact]
    public async Task Requires_authentication()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/users?query=abc");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Eight hex characters: unique enough for a test run and valid inside a user name.
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
}
