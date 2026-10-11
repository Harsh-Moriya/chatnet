using System.Net.Http.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChatNET.API.Tests.Conversations;

// A query can run in one round trip and still be expensive. This test guards the cost of the
// conversation list: the work it does must depend on how many conversations the caller has,
// not on how many messages exist in the whole system.
//
// It fills the database with tens of thousands of messages in conversations the caller does not
// belong to, runs the real endpoint, then asks Postgres (EXPLAIN ANALYZE) how many Messages rows
// that exact query read. See note 08 for the full story behind this test.
[Collection(IntegrationTestCollection.Name)]
public class ConversationListPlanTests
{
    private const int UnrelatedConversations = 30;
    private const int MessagesPerConversation = 3000;

    private readonly ChatApiFactory _factory;

    public ConversationListPlanTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_list_reads_messages_of_the_callers_conversations_only_not_every_message()
    {
        var tag = $"plan-{Guid.NewGuid():N}";
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));

        var noiseOwner = await _factory.RegisterUserAsync("noise");
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");

        await SeedUnrelatedMessagesAsync(tag, noiseOwner.Id);
        try
        {
            using var aliceClient = _factory.CreateClientFor(alice);
            var ids = new[]
            {
                (await aliceClient.CreateDirectAsync(bob.Id)).IdOf(),
                (await aliceClient.CreateDirectAsync(carol.Id)).IdOf(),
                (await aliceClient.CreateGroupAsync("Plan test", bob.Id, carol.Id)).IdOf(),
            };
            foreach (var id in ids)
            {
                await _factory.AddMessageAsync(id, bob.Id, "older", DateTime.UtcNow.AddMinutes(-2));
                await _factory.AddMessageAsync(id, bob.Id, "newer", DateTime.UtcNow.AddMinutes(-1));
            }

            using var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
            recorder.Clear();
            var response = await client.GetAsync("/api/conversations");
            response.EnsureSuccessStatusCode();

            // The endpoint must still return the right previews; the plan is only meaningful
            // for a query that gives the right answer.
            var list = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(3, list.GetArrayLength());
            Assert.All(list.EnumerateArray(), c =>
                Assert.Equal("newer", c.GetProperty("lastMessage").GetProperty("content").GetString()));

            var listQuery = Assert.Single(recorder.Commands);
            var rowsRead = await _factory.QueryDbAsync(db =>
                QueryPlan.RowsReadFromTableAsync(db, listQuery, "Messages"));

            // One lookup per conversation reads about 3 rows. The slow form reads every message in
            // the database, tens of thousands here. 100 leaves room for planner variation.
            Assert.True(rowsRead <= 100,
                $"The list query read {rowsRead} Messages rows for a caller with 3 conversations, " +
                $"while {UnrelatedConversations * MessagesPerConversation} messages exist in other conversations.");
        }
        finally
        {
            await RemoveUnrelatedMessagesAsync(tag);
        }
    }

    // Bulk insert with generate_series is far faster than going through the API, and ANALYZE
    // refreshes the statistics so the planner sees the real table sizes, as it would in production.
    private Task SeedUnrelatedMessagesAsync(string tag, Guid ownerId) =>
        _factory.ExecuteDbAsync(async db =>
        {
            var namePattern = tag + "%";

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Conversations"" (""Id"", ""Type"", ""Name"", ""CreatedAt"", ""UpdatedAt"", ""CreatedById"")
                SELECT gen_random_uuid(), 'Group', {tag} || g::text, now(), now(), {ownerId}
                FROM generate_series(1, {UnrelatedConversations}) g");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Content"", ""CreatedAt"")
                SELECT gen_random_uuid(), c.""Id"", {ownerId}, 'noise', now() - (random() * interval '30 days')
                FROM ""Conversations"" c
                CROSS JOIN generate_series(1, {MessagesPerConversation}) s
                WHERE c.""Name"" LIKE {namePattern}");

            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""Messages""");
            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""Conversations""");
        });

    // Messages are deleted with their conversations (cascade).
    private Task RemoveUnrelatedMessagesAsync(string tag) =>
        _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""Conversations"" WHERE ""Name"" LIKE {tag + "%"}"));
}
