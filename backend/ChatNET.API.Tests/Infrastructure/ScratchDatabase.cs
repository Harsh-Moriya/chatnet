using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChatNET.API.Tests.Infrastructure;

// A throwaway database in the test Postgres, so a migration can be run step by step, up and down,
// without touching the database the other tests share. Dropped when disposed.
public sealed class ScratchDatabase : IAsyncDisposable
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

    // The full ID of a migration (timestamp and name) from its name, so a test about one migration is not
    // affected by migrations added after it.
    public string MigrationNamed(string name) =>
        _db.Database.GetMigrations().Single(m => m.EndsWith($"_{name}", StringComparison.Ordinal));

    // The migration applied just before the given one.
    public string MigrationBefore(string migrationId) =>
        _db.Database.GetMigrations().TakeWhile(m => m != migrationId).Last();

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
        await WithModelColumnsAsync(async () =>
        {
            _db.Users.AddRange(users);
            await _db.SaveChangesAsync();
        });
    }

    // For arranging rows with the application's own model, at a schema the model still matches.
    public async Task AddAsync(params object[] entities)
    {
        await WithModelColumnsAsync(async () =>
        {
            _db.AddRange(entities);
            await _db.SaveChangesAsync();
        });
    }

    // Columns that migrations added to tables the model writes. A schema from before such a migration lacks them, and the model
    // writes them on every insert, so they are added for the time of the insert and dropped again: the rows are as they would have
    // been, and the schema is the one the test asked for. (AvatarKeys renamed the picture columns; AddConversationKeyEpochs added
    // the key epoch of a conversation; EncryptMessages replaced the text of a message with a blob; AddChangeSequence numbered the changes of
    // a conversation.)
    private static readonly (string Table, string Column, string Definition)[] ModelColumns =
    [
        ("AspNetUsers", "AvatarKey", "text"),
        ("Conversations", "AvatarKey", "text"),
        ("Conversations", "KeyEpoch", "integer NOT NULL DEFAULT 0"),
        ("Conversations", "KeyRotationRequired", "boolean NOT NULL DEFAULT false"),
        ("Conversations", "ChangeSeq", "bigint NOT NULL DEFAULT 0"),
        ("Messages", "Ciphertext", "bytea"),
        ("Messages", "KeyEpoch", "integer NOT NULL DEFAULT 0"),
        ("Messages", "MentionedUserIds", "uuid[] NOT NULL DEFAULT ARRAY[]::uuid[]"),
        ("Messages", "ChangeSeq", "bigint NOT NULL DEFAULT 0"),
    ];

    private async Task WithModelColumnsAsync(Func<Task> insert)
    {
        var added = new List<(string Table, string Column)>();

        foreach (var (table, column, definition) in ModelColumns)
        {
            if (await ColumnExistsAsync(table, column))
                continue;

            await ExecuteAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
            added.Add((table, column));
        }

        try
        {
            await insert();
        }
        finally
        {
            foreach (var (table, column) in added)
                await ExecuteAsync($"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\"");
        }
    }

    public Task ExecuteAsync(string sql) => _db.Database.ExecuteSqlRawAsync(sql);

    public async Task<string> EmailIndexDefinitionAsync() =>
        (string)(await ScalarAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'EmailIndex'"))!;

    public async Task<string> IndexDefinitionAsync(string index) =>
        (string)(await ScalarAsync($"SELECT indexdef FROM pg_indexes WHERE indexname = '{index}'"))!;

    public async Task<bool> ColumnExistsAsync(string table, string column) =>
        (bool)(await ScalarAsync(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}')"))!;

    public async Task<bool> IndexExistsAsync(string index) =>
        (bool)(await ScalarAsync($"SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = '{index}')"))!;

    public async Task<bool> TableExistsAsync(string table) =>
        (bool)(await ScalarAsync($"SELECT to_regclass('public.\"{table}\"') IS NOT NULL"))!;

    public async Task<List<string>> AppliedMigrationsAsync() =>
        (await _db.Database.GetAppliedMigrationsAsync()).ToList();

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_db.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();

        await using var admin = new NpgsqlConnection(_adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
