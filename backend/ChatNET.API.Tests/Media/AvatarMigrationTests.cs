using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Media;

// A person and a group used to keep an address for their picture (AvatarUrl, never written); they now keep the key of the
// picture in the bucket (AvatarKey). The columns are renamed, not dropped and added, so nothing that was stored is lost.
[Collection(IntegrationTestCollection.Name)]
public class AvatarMigrationTests
{
    private readonly ChatApiFactory _factory;

    public AvatarMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_renames_both_picture_columns_keeping_their_values_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AvatarKeys");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.True(await scratch.ColumnExistsAsync("AspNetUsers", "AvatarUrl"));
        Assert.True(await scratch.ColumnExistsAsync("Conversations", "AvatarUrl"));

        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"UserName\", \"EmailConfirmed\", \"PhoneNumberConfirmed\", \"TwoFactorEnabled\", " +
            "\"LockoutEnabled\", \"AccessFailedCount\", \"DisplayName\", \"IsDeactivated\", \"AvatarUrl\") " +
            $"VALUES ('{userId}', 'someone', false, false, false, false, 0, 'Someone', false, 'old/user/picture')");
        await scratch.ExecuteAsync(
            "INSERT INTO \"Conversations\" (\"Id\", \"Type\", \"Name\", \"CreatedAt\", \"UpdatedAt\", \"CreatedById\", \"AvatarUrl\") " +
            $"VALUES ('{conversationId}', 'Group', 'Crew', now(), now(), '{userId}', 'old/group/picture')");

        await scratch.MigrateToAsync(migration);

        Assert.False(await scratch.ColumnExistsAsync("AspNetUsers", "AvatarUrl"));
        Assert.False(await scratch.ColumnExistsAsync("Conversations", "AvatarUrl"));
        Assert.Equal("old/user/picture", await scratch.ScalarAsync(
            $"SELECT \"AvatarKey\" FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'"));
        Assert.Equal("old/group/picture", await scratch.ScalarAsync(
            $"SELECT \"AvatarKey\" FROM \"Conversations\" WHERE \"Id\" = '{conversationId}'"));

        await scratch.MigrateToAsync(previous);

        Assert.False(await scratch.ColumnExistsAsync("AspNetUsers", "AvatarKey"));
        Assert.Equal("old/user/picture", await scratch.ScalarAsync(
            $"SELECT \"AvatarUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'"));
        Assert.Equal("old/group/picture", await scratch.ScalarAsync(
            $"SELECT \"AvatarUrl\" FROM \"Conversations\" WHERE \"Id\" = '{conversationId}'"));
    }
}
