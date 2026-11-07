using System.Security.Cryptography;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChatNET.API.Tests.Infrastructure;

// What a test needs to arrange and to look at around password resets.
public static class PasswordResetApi
{
    // Puts a reset link straight into the table, for the states the API does not produce (an expired link, a used one). Returns
    // the token itself, as it would have arrived in the email.
    public static async Task<string> AddResetTokenAsync(
        this ChatApiFactory factory, Guid userId, DateTime? expiresAt = null, DateTime? usedAt = null, DateTime? createdAt = null)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var row = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = TokenHash.Of(token),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(30),
            UsedAt = usedAt,
        };

        await factory.ExecuteDbAsync(async db =>
        {
            db.PasswordResetTokens.Add(row);
            await db.SaveChangesAsync();
        });

        return token;
    }

    public static Task<List<PasswordResetToken>> ResetTokensOfAsync(this ChatApiFactory factory, Guid userId) =>
        factory.QueryDbAsync(db => db.PasswordResetTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync());

    public static Task<PasswordResetToken?> ResetTokenAsync(this ChatApiFactory factory, string token)
    {
        var hash = TokenHash.Of(token);
        return factory.QueryDbAsync(db => db.PasswordResetTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash));
    }

    // Holds a person's row the way a password reset does: a transaction, on a connection of its own, that has locked the row for
    // update. What runs meanwhile on the API's side shows whether it waits. The lock is released by Commit or by disposing.
    public static async Task<HeldUserRow> HoldUserRowAsync(this ChatApiFactory factory, Guid userId)
    {
        var connection = new NpgsqlConnection(factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();

        await using (var command = new NpgsqlCommand("SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }

        return new HeldUserRow(connection, transaction);
    }
}

public sealed class HeldUserRow : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public HeldUserRow(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    // A change made while holding the row, as the reset makes it.
    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        await command.ExecuteNonQueryAsync();
    }

    public Task CommitAsync() => _transaction.CommitAsync();

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
