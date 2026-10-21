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
        _db.Users.AddRange(users);
        await _db.SaveChangesAsync();
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

    private async Task<object?> ScalarAsync(string sql)
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
