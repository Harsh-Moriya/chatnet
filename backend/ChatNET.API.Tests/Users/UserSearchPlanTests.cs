using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Users;

// The people search in the new conversation dialog matches the start of a handle or a display name with ILIKE. Without a
// trigram index every search read every user. The test fills the table with tens of thousands of people, runs the real endpoint,
// and asks Postgres how many rows of AspNetUsers that exact query read (the technique of notes 08 and 12).
[Collection(IntegrationTestCollection.Name)]
public class UserSearchPlanTests : IAsyncLifetime
{
    private const int Strangers = 30000;

    private readonly ChatApiFactory _factory;
    private readonly string _tag = $"pl{Guid.NewGuid():N}"[..12];
    private readonly CommandRecorder _recorder = new();

    public UserSearchPlanTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ExecuteDbAsync(async db =>
    {
        // Handles that begin the same way, and display names that do not, so both columns are searched for real. Every email
        // and handle is unique, as the table requires.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""AspNetUsers"" (""Id"", ""UserName"", ""NormalizedUserName"", ""Email"", ""NormalizedEmail"", ""EmailConfirmed"",
                                         ""PhoneNumberConfirmed"", ""TwoFactorEnabled"", ""LockoutEnabled"", ""AccessFailedCount"",
                                         ""DisplayName"", ""IsDeactivated"")
            SELECT gen_random_uuid(),
                   'bulk_' || s::text || '_' || {_tag}, upper('bulk_' || s::text || '_' || {_tag}),
                   'bulk' || s::text || {_tag} || '@example.test', upper('bulk' || s::text || {_tag} || '@example.test'),
                   false, false, false, false, 0,
                   'Person ' || s::text || ' ' || {_tag}, false
            FROM generate_series(1, {Strangers}) s");

        // VACUUM as well as ANALYZE: rows just inserted sit in the pending list of a GIN index until a vacuum merges them, and
        // while they do Postgres counts reading that list as part of the cost of using the index.
        await db.Database.ExecuteSqlRawAsync(@"VACUUM (ANALYZE) ""AspNetUsers""");
    });

    public Task DisposeAsync() => _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
        $@"DELETE FROM ""AspNetUsers"" WHERE ""UserName"" LIKE {"bulk\\_%\\_" + _tag}"));

    [Theory]
    [InlineData("handle")]
    [InlineData("name")]
    public async Task A_search_for_a_rare_start_reads_about_as_many_users_as_it_finds(string by)
    {
        var searcher = await _factory.RegisterUserAsync("searcher");
        var wanted = await _factory.RegisterUserAsync($"{_tag}_zed", $"Zedekiah {_tag}");
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(_recorder))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", searcher.Token);
        _recorder.Clear();

        var found = await client.SearchUsersAsync(by == "handle" ? $"{_tag}_ze" : "Zedekiah");

        Assert.Equal(wanted.Id, Assert.Single(found).GetProperty("id").GetGuid());
        var query = Assert.Single(_recorder.Commands, c => c.Sql.Contains("ILIKE") || c.Sql.Contains("ilike"));
        var rows = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, query, "AspNetUsers"));

        // Reading every user would be about 30,000.
        var plan = rows <= 200 ? string.Empty : await _factory.QueryDbAsync(db => QueryPlan.ExplainAsync(db, query));
        Assert.True(rows <= 200, $"Searching for people read {rows} AspNetUsers rows to find 1, from {Strangers} users.{Environment.NewLine}{query.Sql}{Environment.NewLine}{plan}");
    }
}
