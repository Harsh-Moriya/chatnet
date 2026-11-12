using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// A feed that is correct can still cost far more than it returns. A page must cost the same for a conversation with six thousand
// messages as for one with a hundred, wherever in the conversation the cursor is: the unique index on (ConversationId, ChangeSeq) lets
// Postgres start at the cursor and stop after the page. The test fills one conversation, reads pages through the real endpoint, and asks
// Postgres (EXPLAIN ANALYZE) how many Messages rows the exact query read.
[Collection(IntegrationTestCollection.Name)]
public class ChangesFeedPlanTests
{
    private const int MessagesInTheConversation = 6000;
    private const int NoiseConversations = 10;
    private const int MessagesPerNoiseConversation = 500;

    private readonly ChatApiFactory _factory;

    public ChangesFeedPlanTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(3000L)]
    [InlineData(5900L)]
    public async Task A_page_from_anywhere_in_a_long_conversation_reads_about_one_page_of_messages(long since)
    {
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var setup = _factory.CreateClientFor(alice);
        var conversation = (await setup.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(conversation);
        await _factory.AddMessagesAsync(conversation, bob.Id, MessagesInTheConversation, DateTime.UtcNow.AddHours(-3), TimeSpan.FromSeconds(1), "bulk");

        // Other conversations on the same table, so an index that did not start at the cursor would have more to read than this one.
        for (var i = 0; i < NoiseConversations; i++)
        {
            var carol = await _factory.RegisterUserAsync($"noise{i}");
            using var noiseSetup = _factory.CreateClientFor(carol);
            var noise = (await noiseSetup.CreateDirectAsync(bob.Id)).IdOf();
            await _factory.BackdateJoinAsync(noise);
            await _factory.AddMessagesAsync(noise, bob.Id, MessagesPerNoiseConversation, DateTime.UtcNow.AddHours(-3), TimeSpan.FromSeconds(1), "noise");
        }

        // The planner chooses by the sizes of the tables as it knows them, and a table that was just filled has none until it is analysed:
        // as it would have in production, where autovacuum keeps them.
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlRawAsync(@"ANALYZE ""Messages"""));

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        recorder.Clear();
        var page = await client.GetChangesAsync(conversation, since);

        // The plan only means something for a query that gives the right answer.
        var expected = (int)Math.Min(100, MessagesInTheConversation - since);
        Assert.Equal(expected, page.Changes.Count);
        Assert.Equal(since + 1, page.Changes[0].ChangeSeqOf());
        Assert.Equal(since + expected, page.Changes[^1].ChangeSeqOf());

        var changesQuery = Assert.Single(recorder.Commands, c => c.Sql.Contains("FROM \"Messages\"") && c.Sql.Contains("\"ChangeSeq\""));
        var rowsRead = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, changesQuery, "Messages"));

        // A page is 101 rows (one more than is returned, to know whether there is another); the allowance covers planner variation.
        // Reading the whole conversation would be 6,000.
        Assert.True(rowsRead <= 250,
            $"Reading the changes after {since} read {rowsRead} Messages rows to return {expected}, from a conversation with " +
            $"{MessagesInTheConversation} messages.\n" + await _factory.QueryDbAsync(db => QueryPlan.ExplainAsync(db, changesQuery)));
    }
}
