using System.Net.Http.Json;
using System.Security.Cryptography;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Services;
using ChatNET.API.Outbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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


    public static Task<HttpResponseMessage> ForgotAsync(this HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/auth/forgot-password", new { email });

    // One pass of the outbox relay of this host, by hand: the shared test host's relay ticks once an hour so it never runs on
    // its own while other tests do.
    public static Task RunRelayAsync(this WebApplicationFactory<Program> host) => host.Services
        .GetServices<IHostedService>()
        .OfType<OutboxRelayService>()
        .Single()
        .RunOnceAsync(CancellationToken.None);

    // The token in the link of an email, read from its text part.
    public static string TokenIn(ReceivedMail mail)
    {
        var match = System.Text.RegularExpressions.Regex.Match(mail.Text, @"/reset-password#token=([A-Za-z0-9_-]+)");
        Assert.True(match.Success, "The email has no reset link.");
        return match.Groups[1].Value;
    }

    // How many requests for a link were saved for an address that gets no email (nobody has it, the account is deactivated,
    // or it was over the limit).
    public static Task<int> ResetRowsForNobodyAsync(this ChatApiFactory factory) => factory.QueryDbAsync(db =>
        db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM "OutboxMessages"
            WHERE "Kind" = 'PasswordResetRequested' AND "Payload" ->> 'userId' IS NULL
            """).SingleAsync());

    public static Task DeactivateAsync(this ChatApiFactory factory, Guid userId) =>
        factory.ExecuteDbAsync(db => db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeactivated, true)));

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
