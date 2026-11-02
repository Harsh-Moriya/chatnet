using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Search;

// Search needs one index on the conversation and the words together, and the extension that lets a GIN index hold a
// conversation id. The migration creates both and takes both away, and the index is exactly the expression the query writes:
// an index on any other expression would exist and never be used.
[Collection(IntegrationTestCollection.Name)]
public class SearchIndexMigrationTests
{
    private readonly ChatApiFactory _factory;

    public SearchIndexMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_creates_the_index_and_its_extension_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AddMessageSearchIndexes");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.IndexExistsAsync("IX_Messages_Search"));

        await scratch.MigrateToAsync(migration);

        Assert.True(await scratch.IndexExistsAsync("IX_Messages_Search"));
        Assert.True((bool)(await scratch.ScalarAsync("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'btree_gin')"))!);

        await scratch.MigrateToAsync(previous);

        Assert.False(await scratch.IndexExistsAsync("IX_Messages_Search"));
        Assert.False((bool)(await scratch.ScalarAsync("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'btree_gin')"))!);
    }

    [Fact]
    public async Task The_index_is_on_the_conversation_and_the_words_written_the_way_the_query_writes_them()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("AddMessageSearchIndexes"));

        var definition = await scratch.IndexDefinitionAsync("IX_Messages_Search");

        Assert.Contains("USING gin", definition);
        Assert.Contains("\"ConversationId\"", definition);
        Assert.Contains("to_tsvector('simple'::regconfig, COALESCE(\"Content\", ''::text))", definition);
    }
}
