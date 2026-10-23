using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Notifications;

// Opening the bell must cost the same for someone with ten thousand notifications as for someone with ten. The
// list walks the (UserId, IsRead, CreatedAt DESC) index once for unread and once for read rows and stops after a
// page of each, and the badge counts no further than its cap. As with history, the test asks Postgres how many
// rows the real queries read (EXPLAIN ANALYZE), which does not depend on how busy the machine is.
[Collection(IntegrationTestCollection.Name)]
public class NotificationListPlanTests
{
    private const int NotificationsOfTheUser = 6000;
    private const int PageSize = 30;

    private readonly ChatApiFactory _factory;

    public NotificationListPlanTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Opening_the_list_reads_about_two_pages_of_notifications_not_all_of_them()
    {
        var (me, recorder, host, client) = await SetUpAsync();
        using var _ = host;
        using var __ = client;
        try
        {
            recorder.Clear();
            var list = await client.GetNotificationsAsync();

            // Half read and half unread, interleaved: the answer is the 30 newest of both together.
            Assert.Equal(PageSize, list.ItemsOf().Count);
            Assert.Equal(100, list.UnreadCountOf());

            var winners = Assert.Single(recorder.Commands, c => c.Sql.Contains("UNION ALL") && c.Sql.Contains("\"Notifications\""));
            var rowsRead = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, winners, "Notifications"));

            // Two walks of one page each; the allowance covers planner variation. Reading everything would be 6,000.
            Assert.True(rowsRead <= 4 * PageSize,
                $"Opening the list read {rowsRead} Notifications rows to return {PageSize}, from {NotificationsOfTheUser}.");
        }
        finally
        {
            await RemoveAsync(me);
        }
    }

    [Fact]
    public async Task A_page_from_the_middle_of_a_long_list_costs_the_same()
    {
        var (me, recorder, host, client) = await SetUpAsync();
        using var _ = host;
        using var __ = client;
        try
        {
            var anchor = await _factory.QueryDbAsync(db => db.Notifications
                .Where(n => n.UserId == me.Id)
                .OrderByDescending(n => n.CreatedAt)
                .Skip(NotificationsOfTheUser / 2)
                .Select(n => n.Id)
                .FirstAsync());

            recorder.Clear();
            var page = await client.GetNotificationsAsync(before: anchor);

            Assert.Equal(PageSize, page.ItemsOf().Count);

            var winners = Assert.Single(recorder.Commands, c => c.Sql.Contains("UNION ALL") && c.Sql.Contains("\"Notifications\""));
            var rowsRead = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, winners, "Notifications"));

            Assert.True(rowsRead <= 4 * PageSize,
                $"A page from the middle read {rowsRead} Notifications rows to return {PageSize}, from {NotificationsOfTheUser}.");
        }
        finally
        {
            await RemoveAsync(me);
        }
    }

    [Fact]
    public async Task The_badge_counts_no_further_than_its_cap()
    {
        var (me, recorder, host, client) = await SetUpAsync();
        using var _ = host;
        using var __ = client;
        try
        {
            recorder.Clear();
            await client.GetNotificationsAsync();

            var count = Assert.Single(recorder.Commands, c => c.Sql.Contains("count(", StringComparison.OrdinalIgnoreCase) && c.Sql.Contains("\"Notifications\""));
            var rowsRead = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, count, "Notifications"));

            // The planner may walk the index or, when this many rows are unread, scan the table and stop at the hundredth
            // match; either way the cap ends the work. Counting all of them would read 3,000 rows (or the whole table).
            Assert.True(rowsRead <= 1000,
                $"Counting the badge read {rowsRead} Notifications rows, with a cap of 100 and {NotificationsOfTheUser / 2} unread.");
        }
        finally
        {
            await RemoveAsync(me);
        }
    }

    [Fact]
    public async Task Opening_the_list_runs_a_fixed_number_of_queries()
    {
        var (me, recorder, host, client) = await SetUpAsync();
        using var _ = host;
        using var __ = client;
        try
        {
            recorder.Clear();
            await client.GetNotificationsAsync();

            // The page of positions, the rows for those, and the count.
            Assert.True(recorder.Commands.Count <= 3, $"Opening the list ran {recorder.Commands.Count} queries; expected at most 3.");
        }
        finally
        {
            await RemoveAsync(me);
        }
    }

    // A user with thousands of notifications of both kinds, interleaved in time, a few thousand more for other
    // people, and a client whose queries are recorded.
    private async Task<(TestUser Me, CommandRecorder Recorder, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host, HttpClient Client)> SetUpAsync()
    {
        var me = await _factory.RegisterUserAsync("me");
        var other = await _factory.RegisterUserAsync("other");
        using var otherClient = _factory.CreateClientFor(other);
        var group = (await otherClient.CreateGroupAsync("Plan", me.Id)).IdOf();
        await _factory.ExecuteDbAsync(async db =>
        {
            await db.Notifications.Where(n => n.UserId == me.Id).ExecuteDeleteAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Notifications"" (""Id"", ""UserId"", ""ActorId"", ""ConversationId"", ""Type"", ""IsRead"", ""CreatedAt"")
                SELECT gen_random_uuid(), {me.Id}, {other.Id}, {group}, 'Mention', (s % 2 = 0), now() - (s * interval '1 second')
                FROM generate_series(1, {NotificationsOfTheUser}) s");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Notifications"" (""Id"", ""UserId"", ""ActorId"", ""ConversationId"", ""Type"", ""IsRead"", ""CreatedAt"")
                SELECT gen_random_uuid(), {other.Id}, {me.Id}, {group}, 'Mention', (random() < 0.5), now() - (random() * interval '30 days')
                FROM generate_series(1, 4000)");

            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""Notifications""");
        });

        var recorder = new CommandRecorder();
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", me.Token);

        return (me, recorder, host, client);
    }

    // Both users' rows go (the other person's name this user as their actor), so the next test starts from the same table.
    private Task RemoveAsync(TestUser me) =>
        _factory.ExecuteDbAsync(db => db.Notifications.Where(n => n.UserId == me.Id || n.ActorId == me.Id).ExecuteDeleteAsync());
}
