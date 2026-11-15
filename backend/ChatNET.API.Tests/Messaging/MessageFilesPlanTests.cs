using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Messaging;

// The gallery must cost about the messages it returns, however long the conversation is and however few of its messages have files.
// Without the partial index on the messages that have files, finding the first page of a conversation where files are rare would
// walk every message of it: that is the case worth pinning.
//
// The test fills one conversation with thousands of messages, marks some of them as having files, loads the list through the real
// endpoint, and asks Postgres (EXPLAIN ANALYZE) how many Messages rows that exact query read.
[Collection(IntegrationTestCollection.Name)]
public class MessageFilesPlanTests
{
    private const int MessagesInTheConversation = 6000;
    private const int NoiseConversations = 20;
    private const int MessagesPerNoiseConversation = 500;

    private readonly ChatApiFactory _factory;

    public MessageFilesPlanTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Every "filesEvery"th message has files; "bulk N" is N seconds older than the newest, so the file messages are
    // "bulk (filesEvery - 1)", "bulk (2 * filesEvery - 1)" and so on, newest first.
    private async Task<(List<System.Text.Json.JsonElement> Page, long RowsRead)> FirstPageAsync(int filesEvery, string? cursorContent = null)
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
        await SeedAsync(tag, conversation, bob.Id, filesEvery);
        try
        {
            using var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
            Guid? before = null;
            if (cursorContent is not null)
            {
                before = await _factory.QueryDbAsync(db => db.Messages
                    .Where(m => m.ConversationId == conversation && m.Ciphertext == FakeMessage.Blob(1, cursorContent))
                    .Select(m => m.Id)
                    .SingleAsync());
            }

            recorder.Clear();
            var page = await client.GetFilesAsync(conversation, before);

            var listQuery = Assert.Single(recorder.Commands, c => c.Sql.Contains("\"HasFiles\""));
            var rowsRead = await _factory.QueryDbAsync(db =>
                QueryPlan.RowsReadFromTableAsync(db, listQuery, "Messages"));

            return (page, rowsRead);
        }
        finally
        {
            await RemoveAsync(tag, conversation);
        }
    }

    [Fact]
    public async Task Listing_the_files_of_a_conversation_where_they_are_rare_reads_only_the_messages_that_have_them()
    {
        // Three file messages among 6,000: bulk 1999, 3999 and 5999.
        var (page, rowsRead) = await FirstPageAsync(filesEvery: 2000);

        Assert.Equal(["bulk 1999", "bulk 3999", "bulk 5999"], page.Select(m => m.ContentOf()));
        Assert.True(rowsRead <= 10,
            $"Listing the files read {rowsRead} Messages rows to return 3, from a conversation with {MessagesInTheConversation} messages.");
    }

    [Fact]
    public async Task Listing_a_page_of_files_reads_about_one_page_of_messages()
    {
        // 120 file messages, one in every 50: the first page is bulk 49, 99, ... 2499.
        var (page, rowsRead) = await FirstPageAsync(filesEvery: 50);

        Assert.Equal(50, page.Count);
        Assert.Equal("bulk 49", page[0].ContentOf());
        Assert.Equal("bulk 2499", page[^1].ContentOf());
        Assert.True(rowsRead <= 100,
            $"Listing the files read {rowsRead} Messages rows to return 50, from a conversation with {MessagesInTheConversation} messages.");
    }

    // The cursor comparison has to start the index walk at the cursor, not at the newest file.
    [Fact]
    public async Task Listing_a_page_from_the_middle_of_the_files_reads_about_one_page_of_messages()
    {
        // The page before "bulk 2499" is bulk 2549, 2599 ... 4999.
        var (page, rowsRead) = await FirstPageAsync(filesEvery: 50, cursorContent: "bulk 2499");

        Assert.Equal(50, page.Count);
        Assert.Equal("bulk 2549", page[0].ContentOf());
        Assert.Equal("bulk 4999", page[^1].ContentOf());
        Assert.True(rowsRead <= 100,
            $"Listing a page of files read {rowsRead} Messages rows to return 50, from a conversation with {MessagesInTheConversation} messages.");
    }

    // generate_series is far faster than the API, and ANALYZE gives the planner the real table sizes, as it would have in production.
    private Task SeedAsync(string tag, Guid conversationId, Guid senderId, int filesEvery) =>
        _factory.ExecuteDbAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Ciphertext"", ""KeyEpoch"", ""ClientMessageId"", ""CreatedAt"", ""ChangeSeq"", ""HasFiles"")
                SELECT gen_random_uuid(), {conversationId}, {senderId}, decode('0101' || '00000001' || repeat('00', 12), 'hex') || convert_to('bulk ' || (s - 1)::text, 'UTF8') || decode(repeat('00', 80), 'hex'), 1, gen_random_uuid(),
                       now() - (s * interval '1 second'), {MessagesInTheConversation} + 1 - s, s % {filesEvery} = 0
                FROM generate_series(1, {MessagesInTheConversation}) s");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""MessageAttachments"" (""Id"", ""MessageId"", ""StorageKey"", ""FileSizeBytes"", ""CreatedAt"")
                SELECT gen_random_uuid(), m.""Id"", 'attachments/plan/' || m.""Id""::text, 100, m.""CreatedAt""
                FROM ""Messages"" m
                WHERE m.""ConversationId"" = {conversationId} AND m.""HasFiles""");

            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""Conversations"" SET ""ChangeSeq"" = {MessagesInTheConversation} WHERE ""Id"" = {conversationId}");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Conversations"" (""Id"", ""Type"", ""Name"", ""CreatedAt"", ""UpdatedAt"", ""CreatedById"")
                SELECT gen_random_uuid(), 'Group', {tag} || g::text, now(), now(), {senderId}
                FROM generate_series(1, {NoiseConversations}) g");

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Messages"" (""Id"", ""ConversationId"", ""SenderId"", ""Ciphertext"", ""KeyEpoch"", ""ClientMessageId"", ""CreatedAt"", ""ChangeSeq"", ""HasFiles"")
                SELECT gen_random_uuid(), c.""Id"", {senderId}, decode('0101' || '00000001' || repeat('00', 12), 'hex') || convert_to('noise', 'UTF8') || decode(repeat('00', 80), 'hex'), 1, gen_random_uuid(), now() - (random() * interval '30 days'), s, s % 10 = 0
                FROM ""Conversations"" c
                CROSS JOIN generate_series(1, {MessagesPerNoiseConversation}) s
                WHERE c.""Name"" LIKE {tag + "%"}");

            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""Conversations"" SET ""ChangeSeq"" = {MessagesPerNoiseConversation} WHERE ""Name"" LIKE {tag + "%"}");

            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""Messages""");
            await db.Database.ExecuteSqlRawAsync(@"ANALYZE ""MessageAttachments""");
        });

    // Messages are deleted with their conversations (cascade), and their attachments with them.
    private Task RemoveAsync(string tag, Guid conversationId) =>
        _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""Conversations"" WHERE ""Name"" LIKE {tag + "%"} OR ""Id"" = {conversationId}"));
}
