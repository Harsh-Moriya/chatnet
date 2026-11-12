using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Messaging;

// The server no longer searches the text of messages. The migration takes away the full text index on them and the extension that
// index needed, and puts both back when undone.
[Collection(IntegrationTestCollection.Name)]
public class MessageSearchIndexRemovalTests
{
    private const string Index = "IX_Messages_Search";
    private const string Extension = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'btree_gin')";

    private readonly ChatApiFactory _factory;

    public MessageSearchIndexRemovalTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_removes_the_text_index_and_its_extension_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("RemoveMessageSearchIndexes");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.True(await scratch.IndexExistsAsync(Index));
        Assert.True((bool)(await scratch.ScalarAsync(Extension))!);

        await scratch.MigrateToAsync(migration);

        Assert.False(await scratch.IndexExistsAsync(Index));
        Assert.False((bool)(await scratch.ScalarAsync(Extension))!);

        await scratch.MigrateToAsync(previous);

        Assert.Contains("to_tsvector", await scratch.IndexDefinitionAsync(Index));
        Assert.True((bool)(await scratch.ScalarAsync(Extension))!);
    }

    [Fact]
    public async Task The_search_endpoint_is_gone()
    {
        var user = await _factory.RegisterUserAsync("seeker");
        using var client = _factory.CreateClientFor(user);

        var response = await client.GetAsync("/api/search/messages?q=anything");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
