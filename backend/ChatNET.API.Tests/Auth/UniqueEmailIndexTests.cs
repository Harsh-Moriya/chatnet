using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChatNET.API.Tests.Auth;

// Registration serialises requests for one email with a lock (see RegisterHandler), but a lock in
// application code protects only the code that takes it. The database is the final guard: it must
// refuse two accounts with one email whatever creates them. These tests check the index, and the
// migration that adds it, which has to cope with a database that already holds duplicates.
[Collection(IntegrationTestCollection.Name)]
public class UniqueEmailIndexTests
{
    private readonly ChatApiFactory _factory;

    public UniqueEmailIndexTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### The rule #####

    [Fact]
    public async Task The_database_refuses_a_second_account_with_the_same_email()
    {
        var email = $"dup_{Guid.NewGuid():N}@example.test";
        await _factory.ExecuteDbAsync(async db =>
        {
            db.Users.Add(User(email));
            await db.SaveChangesAsync();
        });

        var failure = await Record.ExceptionAsync(() => _factory.ExecuteDbAsync(async db =>
        {
            db.Users.Add(User(email));
            await db.SaveChangesAsync();
        }));

        var violation = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(failure).InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
        Assert.Equal("EmailIndex", violation.ConstraintName);
    }

    // Identity normalises an email to upper case for lookups, so the index is on that column and
    // addresses that differ only in case are the same address.
    [Fact]
    public async Task Addresses_that_differ_only_in_case_count_as_the_same()
    {
        var local = $"case_{Guid.NewGuid():N}";
        await _factory.ExecuteDbAsync(async db =>
        {
            db.Users.Add(User($"{local}@example.test"));
            await db.SaveChangesAsync();
        });

        var failure = await Record.ExceptionAsync(() => _factory.ExecuteDbAsync(async db =>
        {
            db.Users.Add(User($"{local.ToUpperInvariant()}@EXAMPLE.TEST"));
            await db.SaveChangesAsync();
        }));

        Assert.IsType<DbUpdateException>(failure);
    }

    [Fact]
    public async Task Different_emails_are_unaffected()
    {
        await _factory.ExecuteDbAsync(async db =>
        {
            db.Users.Add(User($"a_{Guid.NewGuid():N}@example.test"));
            db.Users.Add(User($"b_{Guid.NewGuid():N}@example.test"));
            await db.SaveChangesAsync();
        });
    }

    // ##### The migration #####

    // The tests build their database from the migrations, so a change to the model that was never
    // turned into a migration (for example dropping IsUnique from the configuration) would go
    // unnoticed. This fails when the two disagree.
    [Fact]
    public async Task The_migrations_describe_exactly_the_model()
    {
        var pending = await _factory.QueryDbAsync(db => Task.FromResult(db.Database.HasPendingModelChanges()));

        Assert.False(pending, "The model differs from the migrations. Add a migration or revert the model change.");
    }

    [Fact]
    public async Task The_migration_makes_the_email_index_unique_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.PreviousMigration);
        Assert.DoesNotContain("UNIQUE", await scratch.EmailIndexDefinitionAsync());

        await scratch.MigrateToAsync(scratch.LatestMigration);
        Assert.Contains("UNIQUE", await scratch.EmailIndexDefinitionAsync());

        await scratch.MigrateToAsync(scratch.PreviousMigration);
        Assert.DoesNotContain("UNIQUE", await scratch.EmailIndexDefinitionAsync());
    }

    // A unique index cannot be built over duplicates, and the raw database error does not say what to
    // do about it. The migration checks first, says so in plain words, and changes nothing.
    [Fact]
    public async Task The_migration_refuses_to_run_over_existing_duplicates_and_says_why()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.PreviousMigration);
        await scratch.InsertAsync(User("shared@example.test"), User("shared@example.test"));

        var failure = await Record.ExceptionAsync(() => scratch.MigrateToAsync(scratch.LatestMigration));

        Assert.NotNull(failure);
        Assert.Contains("share an email", FullMessage(failure));
        Assert.DoesNotContain("UNIQUE", await scratch.EmailIndexDefinitionAsync());
        Assert.DoesNotContain(scratch.LatestMigration, await scratch.AppliedMigrationsAsync());
    }

    [Fact]
    public async Task The_migration_runs_once_the_duplicates_are_resolved()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.PreviousMigration);
        await scratch.InsertAsync(User("shared@example.test"), User("shared@example.test"));
        await Record.ExceptionAsync(() => scratch.MigrateToAsync(scratch.LatestMigration));

        await scratch.ExecuteAsync(
            """UPDATE "AspNetUsers" SET "Email" = 'other@example.test', "NormalizedEmail" = 'OTHER@EXAMPLE.TEST' WHERE "Id" = (SELECT "Id" FROM "AspNetUsers" LIMIT 1)""");
        await scratch.MigrateToAsync(scratch.LatestMigration);

        Assert.Contains("UNIQUE", await scratch.EmailIndexDefinitionAsync());
    }

    // ##### Helpers #####

    private static AppUser User(string email) => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"u_{Guid.NewGuid():N}"[..20],
        NormalizedUserName = $"U_{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = "Someone",
        SecurityStamp = Guid.NewGuid().ToString(),
    };

    private static string FullMessage(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
            messages.Add(current.Message);
        return string.Join(" | ", messages);
    }

    // A throwaway database in the test Postgres, so a migration can be run step by step without
    // touching the database the other tests share.
    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _adminConnection;
        private readonly string _name;
        private readonly AppDbContext _db;

        private ScratchDatabase(string adminConnection, string name, AppDbContext db)
        {
            _adminConnection = adminConnection;
            _name = name;
            _db = db;
        }

        public string PreviousMigration => _db.Database.GetMigrations().SkipLast(1).Last();

        public string LatestMigration => _db.Database.GetMigrations().Last();

        public static async Task<ScratchDatabase> CreateAsync(ChatApiFactory factory)
        {
            var adminConnection = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;
            var name = $"scratch_{Guid.NewGuid():N}";

            await using (var admin = new NpgsqlConnection(adminConnection))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
                await create.ExecuteNonQueryAsync();
            }

            var scratchConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name, Pooling = false };
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(scratchConnection.ConnectionString).Options;
            return new ScratchDatabase(adminConnection, name, new AppDbContext(options));
        }

        public Task MigrateToAsync(string migration) => _db.GetService<IMigrator>().MigrateAsync(migration);

        public async Task InsertAsync(params AppUser[] users)
        {
            _db.Users.AddRange(users);
            await _db.SaveChangesAsync();
        }

        public Task ExecuteAsync(string sql) => _db.Database.ExecuteSqlRawAsync(sql);

        public async Task<string> EmailIndexDefinitionAsync()
        {
            await using var connection = new NpgsqlConnection(_db.Database.GetConnectionString());
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE indexname = 'EmailIndex'", connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<List<string>> AppliedMigrationsAsync() =>
            (await _db.Database.GetAppliedMigrationsAsync()).ToList();

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();

            await using var admin = new NpgsqlConnection(_adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
