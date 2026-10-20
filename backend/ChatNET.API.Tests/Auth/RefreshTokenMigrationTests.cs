using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Tests.Auth;

// The refresh token table arrives as a migration, so it has to go up and come back down cleanly, and the
// rules the application relies on (a hash is unique, a deleted user takes their tokens with them) have to
// be rules of the database and not only of the code.
[Collection(IntegrationTestCollection.Name)]
public class RefreshTokenMigrationTests
{
    private readonly ChatApiFactory _factory;

    public RefreshTokenMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_adds_the_table_and_can_be_undone_without_touching_users()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AddRefreshTokens");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.TableExistsAsync("RefreshTokens"));

        await scratch.MigrateToAsync(migration);
        Assert.True(await scratch.TableExistsAsync("RefreshTokens"));
        Assert.True(await scratch.TableExistsAsync("AspNetUsers"));

        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.TableExistsAsync("RefreshTokens"));
        Assert.True(await scratch.TableExistsAsync("AspNetUsers"));
    }

    [Fact]
    public async Task Two_tokens_cannot_share_a_hash()
    {
        var user = await _factory.RegisterUserAsync("dupehash");
        var hash = AuthApi.HashOf(Guid.NewGuid().ToString());

        var failure = await Record.ExceptionAsync(async () =>
        {
            await InsertTokenAsync(user.Id, hash);
            await InsertTokenAsync(user.Id, hash);
        });

        var violation = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(failure).InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_refresh_tokens()
    {
        var user = await _factory.RegisterUserAsync("cascade");
        using var client = _factory.CreateCookielessClient();
        await client.SignInAsync(user);
        Assert.NotEmpty(await _factory.QueryDbAsync(db => db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync()));

        await _factory.ExecuteDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync());

        Assert.Empty(await _factory.QueryDbAsync(db => db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync()));
    }

    private Task InsertTokenAsync(Guid userId, string hash) => _factory.ExecuteDbAsync(async db =>
    {
        db.RefreshTokens.Add(new ChatNET.API.Auth.Models.RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = Guid.NewGuid(),
            TokenHash = hash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
    });
}
