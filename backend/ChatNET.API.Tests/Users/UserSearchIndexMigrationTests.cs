using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Users;

// The people search is served by trigram indexes on the handle and the display name. The migration creates them and the
// extension they need, and takes them away.
[Collection(IntegrationTestCollection.Name)]
public class UserSearchIndexMigrationTests
{
    private readonly ChatApiFactory _factory;

    public UserSearchIndexMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_creates_both_indexes_and_the_extension_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AddUserSearchIndexes");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.IndexExistsAsync("IX_AspNetUsers_UserName_Trigram"));

        await scratch.MigrateToAsync(migration);

        var userName = await scratch.IndexDefinitionAsync("IX_AspNetUsers_UserName_Trigram");
        var displayName = await scratch.IndexDefinitionAsync("IX_AspNetUsers_DisplayName_Trigram");
        Assert.Contains("gin_trgm_ops", userName);
        Assert.Contains("\"UserName\"", userName);
        Assert.Contains("gin_trgm_ops", displayName);
        Assert.Contains("\"DisplayName\"", displayName);

        await scratch.MigrateToAsync(previous);

        Assert.False(await scratch.IndexExistsAsync("IX_AspNetUsers_UserName_Trigram"));
        Assert.False(await scratch.IndexExistsAsync("IX_AspNetUsers_DisplayName_Trigram"));
        Assert.False((bool)(await scratch.ScalarAsync("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm')"))!);
    }
}
