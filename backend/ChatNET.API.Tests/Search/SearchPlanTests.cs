using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Search;

// A search can be correct and still read far more than it returns. It must cost about the same whether the word is rare or
// common, and whether the person is in one conversation or many, and it must never read the conversations they are not in.
// The test fills the database with tens of thousands of messages, runs the real endpoint, then asks Postgres (EXPLAIN ANALYZE)
// how many rows of Messages and MessageAttachments that exact query read. The same technique guards history and the
// conversation list.
[Collection(IntegrationTestCollection.Name)]
public class SearchPlanTests : IAsyncLifetime
{
    private const int BigConversation = 8000;
    private const int MemberConversations = 12;
    private const int MessagesPerMemberConversation = 800;
    private const int OutsideConversations = 20;
    private const int MessagesPerOutsideConversation = 800;
    private const int FilesInTheBigConversation = 3000;

    private readonly ChatApiFactory _factory;
    private readonly string _tag = $"plan-{Guid.NewGuid():N}";
    private readonly CommandRecorder _recorder = new();
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;
    private TestUser _alice = null!;

    public SearchPlanTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(_recorder))));

        _alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var setup = _factory.CreateClientFor(_alice);
        var big = (await setup.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.BackdateJoinAsync(big);
        await SeedAsync(big, bob.Id);

        _client = _host.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _alice.Token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();

        // Messages and their files go with their conversations (cascade).
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""Conversations"" WHERE ""Name"" LIKE {_tag + "%"} OR ""Id"" IN (
                 SELECT ""ConversationId"" FROM ""ConversationMembers"" WHERE ""UserId"" = {_alice.Id})"));
    }

    // Every message of the big conversation says "common"; three say "needle" and "popular"; "bulk N" is N seconds older than the
    // newest. The twelve conversations Alice is also in say "common" too. Twenty conversations she is not in say "popular" in
    // every message and "needle" in three, which a search must not even look at. The newest 3,000 messages of the big
    // conversation carry a file called "photo_N.jpg"; the 77th newest "blueprint.pdf", the 1,500th "archive.pdf".
    private async Task SeedAsync(Guid big, Guid senderId)
    {
        await _factory.ExecuteDbAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Content"", ""CreatedAt"")
                SELECT gen_random_uuid(), {big}, {senderId},
                       CASE WHEN s IN (10, 4000, 7500) THEN 'common needle popular ' ELSE 'common ' END || 'bulk ' || (s - 1)::text,
                       now() - (s * interval '1 second')
                FROM generate_series(1, {BigConversation}) s");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Conversations"" (""Id"", ""Type"", ""Name"", ""CreatedAt"", ""UpdatedAt"", ""CreatedById"")
                SELECT gen_random_uuid(), 'Group', {_tag} || '-in-' || g::text, now(), now(), {senderId}
                FROM generate_series(1, {MemberConversations}) g");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""ConversationMembers"" (""ConversationId"", ""UserId"", ""Role"", ""JoinedAt"")
                SELECT c.""Id"", {_alice.Id}, 'Member', now() - interval '1 day'
                FROM ""Conversations"" c WHERE c.""Name"" LIKE {_tag + "-in-%"}");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Content"", ""CreatedAt"")
                SELECT gen_random_uuid(), c.""Id"", {senderId}, 'common talk ' || s::text, now() - (random() * interval '30 days')
                FROM ""Conversations"" c CROSS JOIN generate_series(1, {MessagesPerMemberConversation}) s
                WHERE c.""Name"" LIKE {_tag + "-in-%"}");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Conversations"" (""Id"", ""Type"", ""Name"", ""CreatedAt"", ""UpdatedAt"", ""CreatedById"")
                SELECT gen_random_uuid(), 'Group', {_tag} || '-out-' || g::text, now(), now(), {senderId}
                FROM generate_series(1, {OutsideConversations}) g");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Content"", ""CreatedAt"")
                SELECT gen_random_uuid(), c.""Id"", {senderId}, CASE WHEN s <= 3 THEN 'needle ' ELSE '' END || 'common popular outside ' || s::text, now() - (random() * interval '30 days')
                FROM ""Conversations"" c CROSS JOIN generate_series(1, {MessagesPerOutsideConversation}) s
                WHERE c.""Name"" LIKE {_tag + "-out-%"}");

            // Files: a lot called photo_N.jpg on messages of the big conversation, one called blueprint.pdf.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""MessageAttachments"" (""Id"", ""MessageId"", ""StorageKey"", ""Type"", ""FileName"", ""FileSizeBytes"", ""CreatedAt"", ""ContentType"")
                SELECT gen_random_uuid(), m.""Id"", 'k/' || m.""Id""::text,
                       'Image', CASE WHEN n = 77 THEN 'blueprint.pdf' WHEN n = 1500 THEN 'archive.pdf' ELSE 'photo_' || n::text || '.jpg' END, 100, now(), 'image/jpeg'
                FROM (SELECT ""Id"", row_number() OVER (ORDER BY ""CreatedAt"" DESC) AS n FROM ""Messages"" WHERE ""ConversationId"" = {big} LIMIT {FilesInTheBigConversation}) m");

            // VACUUM as well as ANALYZE: rows just inserted sit in the pending list of a GIN index until a vacuum merges them, and
            // while they do Postgres counts reading that list as part of the cost of using the index.
            // Statistics from every row and not a random sample of 30,000, so the plans do not depend on which rows were sampled.
            await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Messages"" ALTER COLUMN ""ConversationId"" SET STATISTICS 1000");
            await db.Database.ExecuteSqlRawAsync(@"ALTER INDEX ""IX_Messages_Search"" ALTER COLUMN 2 SET STATISTICS 1000");
            await db.Database.ExecuteSqlRawAsync(@"VACUUM (ANALYZE) ""Messages""");
            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""MessageAttachments""");
            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""ConversationMembers""");
        });
    }

    private async Task<(int Hits, long MessageRows, long FileRows)> RunAsync(string q, Guid? before = null)
    {
        _recorder.Clear();
        var hits = await _client.SearchAsync(q, before: before);

        var search = Assert.Single(_recorder.Commands, c => c.Sql.Contains("ts_headline"));
        var messageRows = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, search, "Messages"));
        var fileRows = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, search, "MessageAttachments"));

        LastPlan = await _factory.QueryDbAsync(db => QueryPlan.ExplainAsync(db, search));

        return (hits.Count, messageRows, fileRows);
    }

    private string LastPlan { get; set; } = string.Empty;

    private string Report(string what, (int Hits, long MessageRows, long FileRows) run) =>
        $"{what} returned {run.Hits}, reading {run.MessageRows} Messages rows and {run.FileRows} MessageAttachments rows, " +
        $"with {BigConversation + MemberConversations * MessagesPerMemberConversation} messages the person may search " +
        $"and {OutsideConversations * MessagesPerOutsideConversation} in conversations they are not in.{Environment.NewLine}{LastPlan}";

    [Fact]
    public async Task A_rare_word_reads_about_as_many_messages_as_the_recent_phase_and_the_index_find()
    {
        var run = await RunAsync("needle");

        // The three in the big conversation. The sixty in the conversations that also say it are not Alice's. The recent phase reads
        // at most 300 messages of each conversation (a few dozen in most here, the window is a day); the older part of the big
        // conversation is found through the full text index. Reading that conversation, or the 16,000 messages of the others,
        // would be thousands.
        Assert.Equal(3, run.Hits);
        Assert.True(run.MessageRows <= 1000, Report("A rare word", run));
    }

    // A word that is in every message of conversations the person is not in is as cheap, because the index starts with the
    // conversation: what other people said is not looked at. (Statistics are kept for the whole table, so Postgres cannot tell that
    // the word is rare in this conversation; it goes by the index being a lookup of one conversation.)
    [Fact]
    public async Task A_word_that_is_common_in_other_conversations_does_not_make_the_search_read_them()
    {
        var run = await RunAsync("popular");

        Assert.Equal(3, run.Hits);
        Assert.True(run.MessageRows <= 1000, Report("A word common in other people's conversations", run));
    }

    [Fact]
    public async Task A_common_word_stops_after_a_page_in_each_conversation()
    {
        var run = await RunAsync("common");

        Assert.Equal(20, run.Hits);

        // 13 conversations, each asked for at most 21 matches. Reading all of them would be 8,000 plus 12 times 800.
        Assert.True(run.MessageRows <= 1000, Report("A common word", run));
    }

    [Fact]
    public async Task A_page_from_the_middle_costs_the_same_as_the_first()
    {
        _recorder.Clear();
        var first = await _client.SearchAsync("common");
        var run = await RunAsync("common", first[^1].HitIdOf());

        Assert.Equal(20, run.Hits);
        Assert.True(run.MessageRows <= 1000, Report("The second page of a common word", run));
    }

    [Fact]
    public async Task A_rare_file_name_among_the_recent_messages_is_found_without_reading_the_history()
    {
        var run = await RunAsync("blueprint");

        // File names are checked among the recent messages of each conversation only.
        Assert.Equal(1, run.Hits);
        Assert.True(run.FileRows <= 4500, Report("A rare file name", run));
        Assert.True(run.MessageRows <= 4500, Report("A rare file name", run));
    }

    // The limit of file name search, recorded: the name of a file is looked for among the newest 300 messages of each
    // conversation, because a file has no conversation of its own to index it by.
    [Fact]
    public async Task A_file_name_older_than_the_recent_messages_is_not_found()
    {
        var run = await RunAsync("archive");

        Assert.Equal(0, run.Hits);
    }

    [Fact]
    public async Task A_common_file_name_stops_after_a_page()
    {
        var run = await RunAsync("photo");

        Assert.Equal(20, run.Hits);
        Assert.True(run.FileRows + run.MessageRows <= 2000, Report("A common file name", run));
    }

    [Fact]
    public async Task One_conversation_named_reads_only_that_conversation()
    {
        var big = await _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.Content!.StartsWith("common needle"))
            .Select(m => m.ConversationId)
            .FirstAsync());

        _recorder.Clear();
        var hits = await _client.SearchAsync("common", conversationId: big);
        var search = Assert.Single(_recorder.Commands, c => c.Sql.Contains("ts_headline"));
        var rows = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, search, "Messages"));

        Assert.Equal(20, hits.Count);
        Assert.True(rows <= 400, $"Searching one conversation read {rows} Messages rows to return 20.");
    }
}
