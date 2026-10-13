using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// A query can be correct and still read far more than it returns. Loading history must cost the
// same for a conversation with ten thousand messages as for one with fifty: the index on
// (ConversationId, CreatedAt DESC) lets Postgres walk back from the newest message and stop.
//
// The test fills one conversation with thousands of messages, loads its history through the real
// endpoint, then asks Postgres (EXPLAIN ANALYZE) how many Messages rows that exact query read.
// The same technique guards the conversation list; see note 08.
[Collection(IntegrationTestCollection.Name)]
public class MessageHistoryPlanTests
{
    private const int MessagesInTheConversation = 6000;
    private const int NoiseConversations = 20;
    private const int MessagesPerNoiseConversation = 500;

    private readonly ChatApiFactory _factory;

    public MessageHistoryPlanTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Loading_history_reads_about_one_page_of_messages_not_the_whole_conversation()
    {
        var tag = $"plan-{Guid.NewGuid():N}";
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var setup = _factory.CreateClientFor(alice);
        var conversation = (await setup.CreateDirectAsync(bob.Id)).IdOf();

        await _factory.BackdateJoinAsync(conversation);
        await SeedAsync(tag, conversation, bob.Id);
        try
        {
            using var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
            recorder.Clear();
            var history = await client.GetHistoryAsync(conversation);

            // The plan only means something for a query that gives the right answer.
            Assert.Equal(50, history.Count);
            Assert.Equal("bulk 49", history[0].ContentOf());
            Assert.Equal("bulk 0", history[^1].ContentOf());

            var messagesQuery = Assert.Single(recorder.Commands, c => c.Sql.Contains("\"Messages\""));
            var rowsRead = await _factory.QueryDbAsync(db =>
                QueryPlan.RowsReadFromTableAsync(db, messagesQuery, "Messages"));

            // One page is 50 rows; the allowance covers planner variation. Reading the whole
            // conversation would be 6,000.
            Assert.True(rowsRead <= 100,
                $"Loading history read {rowsRead} Messages rows to return 50, " +
                $"from a conversation with {MessagesInTheConversation} messages.");
        }
        finally
        {
            await RemoveAsync(tag, conversation);
        }
    }

    // A page in the middle of a long history must cost the same as the latest page: the cursor
    // comparison has to start the index walk at the cursor, not at the newest message. Checked in
    // both directions, since "after" reads the index the other way round.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loading_a_page_from_the_middle_of_a_long_history_reads_about_one_page(bool after)
    {
        var tag = $"plan-{Guid.NewGuid():N}";
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var setup = _factory.CreateClientFor(alice);
        var conversation = (await setup.CreateDirectAsync(bob.Id)).IdOf();

        await _factory.BackdateJoinAsync(conversation);
        await SeedAsync(tag, conversation, bob.Id);
        try
        {
            using var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
            var anchor = await _factory.QueryDbAsync(db => db.Messages
                .Where(m => m.ConversationId == conversation && m.Content == "bulk 3000")
                .Select(m => m.Id)
                .SingleAsync());
            recorder.Clear();
            var page = after
                ? await client.GetHistoryAsync(conversation, after: anchor)
                : await client.GetHistoryAsync(conversation, before: anchor);

            // "bulk N" is N seconds older than the newest, so the page before "bulk 3000" is bulk
            // 3050 down to 3001 (oldest first) and the page after it is bulk 2999 down to 2950.
            Assert.Equal(50, page.Count);
            Assert.Equal(after ? "bulk 2999" : "bulk 3050", page[0].ContentOf());
            Assert.Equal(after ? "bulk 2950" : "bulk 3001", page[^1].ContentOf());

            // The cursor lookup and the page both touch Messages; the page is the one that ran last.
            var pageQuery = recorder.Commands.Last(c => c.Sql.Contains("\"Messages\""));
            var rowsRead = await _factory.QueryDbAsync(db =>
                QueryPlan.RowsReadFromTableAsync(db, pageQuery, "Messages"));

            Assert.True(rowsRead <= 100,
                $"Loading a page {(after ? "after" : "before")} a message read {rowsRead} Messages rows to " +
                $"return 50, from a conversation with {MessagesInTheConversation} messages.");
        }
        finally
        {
            await RemoveAsync(tag, conversation);
        }
    }

    // Newest-first numbering ("bulk 0" is the newest) makes the expected page easy to state.
    // generate_series is far faster than the API, and ANALYZE gives the planner the real table
    // sizes, as it would have in production.
    private Task SeedAsync(string tag, Guid conversationId, Guid senderId) =>
        _factory.ExecuteDbAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Content"", ""CreatedAt"")
                SELECT gen_random_uuid(), {conversationId}, {senderId}, 'bulk ' || (s - 1)::text,
                       now() - (s * interval '1 second')
                FROM generate_series(1, {MessagesInTheConversation}) s");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Conversations"" (""Id"", ""Type"", ""Name"", ""CreatedAt"", ""UpdatedAt"", ""CreatedById"")
                SELECT gen_random_uuid(), 'Group', {tag} || g::text, now(), now(), {senderId}
                FROM generate_series(1, {NoiseConversations}) g");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Content"", ""CreatedAt"")
                SELECT gen_random_uuid(), c.""Id"", {senderId}, 'noise', now() - (random() * interval '30 days')
                FROM ""Conversations"" c
                CROSS JOIN generate_series(1, {MessagesPerNoiseConversation}) s
                WHERE c.""Name"" LIKE {tag + "%"}");

            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""Messages""");
        });

    // Messages are deleted with their conversations (cascade).
    private Task RemoveAsync(string tag, Guid conversationId) =>
        _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""Conversations"" WHERE ""Name"" LIKE {tag + "%"} OR ""Id"" = {conversationId}"));
}
