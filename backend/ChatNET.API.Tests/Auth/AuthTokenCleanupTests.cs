using ChatNET.API.Auth;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ChatNET.API.Tests.Auth;

// An expired refresh token or reset link is useless, so removing old ones only keeps the tables from growing for ever.
// They are kept for a week past their expiry, and a token that can still be used is never touched.
[Collection(IntegrationTestCollection.Name)]
public class AuthTokenCleanupTests
{
    private readonly ChatApiFactory _factory;

    public AuthTokenCleanupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Tokens_that_expired_more_than_a_week_ago_are_removed_and_the_rest_are_kept()
    {
        var user = await _factory.RegisterUserAsync("cleanup");
        var longGone = await SeedAsync(user, expiredAgo: TimeSpan.FromDays(8));
        var recentlyExpired = await SeedAsync(user, expiredAgo: TimeSpan.FromDays(6));
        var stillValid = await SeedAsync(user, expiredAgo: TimeSpan.FromDays(-5));
        var revokedButNotExpired = await SeedAsync(user, expiredAgo: TimeSpan.FromDays(-5), revoked: true);

        await CleanUpAsync();

        var remaining = await _factory.QueryDbAsync(db =>
            db.RefreshTokens.Where(t => t.UserId == user.Id).Select(t => t.Id).ToListAsync());
        Assert.DoesNotContain(longGone, remaining);
        Assert.Contains(recentlyExpired, remaining);
        Assert.Contains(stillValid, remaining);
        Assert.Contains(revokedButNotExpired, remaining);
    }

    [Fact]
    public async Task Running_it_again_with_nothing_to_remove_changes_nothing()
    {
        var user = await _factory.RegisterUserAsync("again");
        var kept = await SeedAsync(user, expiredAgo: TimeSpan.FromDays(-1));

        await CleanUpAsync();
        await CleanUpAsync();

        var remaining = await _factory.QueryDbAsync(db =>
            db.RefreshTokens.Where(t => t.UserId == user.Id).Select(t => t.Id).ToListAsync());
        Assert.Contains(kept, remaining);
        Assert.Equal(remaining.Count, remaining.Distinct().Count());
    }

    [Fact]
    public async Task Reset_links_that_expired_more_than_a_week_ago_are_removed_and_the_rest_are_kept()
    {
        var user = await _factory.RegisterUserAsync("cleanupreset");
        var longGone = await _factory.AddResetTokenAsync(user.Id, expiresAt: DateTime.UtcNow.AddDays(-8));
        var recentlyExpired = await _factory.AddResetTokenAsync(user.Id, expiresAt: DateTime.UtcNow.AddDays(-6));
        var stillValid = await _factory.AddResetTokenAsync(user.Id, expiresAt: DateTime.UtcNow.AddMinutes(20));

        await CleanUpAsync();

        Assert.Null(await _factory.ResetTokenAsync(longGone));
        Assert.NotNull(await _factory.ResetTokenAsync(recentlyExpired));
        Assert.NotNull(await _factory.ResetTokenAsync(stillValid));
    }

    // One kind failing must not leave the other for tomorrow.
    [Fact]
    public async Task A_failure_removing_refresh_tokens_does_not_stop_the_reset_links_from_being_removed()
    {
        var user = await _factory.RegisterUserAsync("cleanupfailure");
        var longGone = await _factory.AddResetTokenAsync(user.Id, expiresAt: DateTime.UtcNow.AddDays(-8));
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRefreshTokenRepository>();
            services.AddScoped<IRefreshTokenRepository, FailingRefreshTokenRepository>();
        }));

        await host.Services.GetServices<IHostedService>().OfType<AuthTokenCleanupService>().Single().CleanUpOnceAsync(CancellationToken.None);

        Assert.Null(await _factory.ResetTokenAsync(longGone));
    }

    // One kind failing must not leave the other for tomorrow.
    [Fact]
    public async Task A_failure_removing_reset_links_does_not_stop_the_refresh_tokens_from_being_removed()
    {
        var user = await _factory.RegisterUserAsync("cleanupfailure2");
        var longGone = await SeedAsync(user, expiredAgo: TimeSpan.FromDays(8));
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordResetTokenRepository>();
            services.AddScoped<IPasswordResetTokenRepository, FailingPasswordResetTokenRepository>();
        }));

        await host.Services.GetServices<IHostedService>().OfType<AuthTokenCleanupService>().Single().CleanUpOnceAsync(CancellationToken.None);

        var remaining = await _factory.QueryDbAsync(db => db.RefreshTokens.Where(t => t.UserId == user.Id).Select(t => t.Id).ToListAsync());
        Assert.DoesNotContain(longGone, remaining);
    }

    private sealed class FailingRefreshTokenRepository : IRefreshTokenRepository
    {
        public Task<int> DeleteExpiredBeforeAsync(DateTime expiredBefore, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database is down.");

        public Task AddAsync(RefreshToken token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> AddIfSecurityStampUnchangedAsync(RefreshToken token, string? expectedSecurityStamp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryRotateAsync(RefreshToken current, RefreshToken successor, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RevokeAllForUserAsync(Guid userId, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailingPasswordResetTokenRepository : IPasswordResetTokenRepository
    {
        public Task<int> DeleteExpiredBeforeAsync(DateTime expiredBefore, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database is down.");

        public Task AddAsync(PasswordResetToken token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> TryConsumeAsync(string tokenHash, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task VoidUnusedAsync(Guid userId, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private Task CleanUpAsync() => _factory.Services
        .GetServices<IHostedService>()
        .OfType<AuthTokenCleanupService>()
        .Single()
        .CleanUpOnceAsync(CancellationToken.None);

    private async Task<Guid> SeedAsync(TestUser user, TimeSpan expiredAgo, bool revoked = false)
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = Guid.NewGuid(),
            TokenHash = AuthApi.HashOf(Guid.NewGuid().ToString()),
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            ExpiresAt = DateTime.UtcNow - expiredAgo,
            RevokedAt = revoked ? DateTime.UtcNow.AddDays(-1) : null,
        };

        await _factory.ExecuteDbAsync(async db =>
        {
            db.RefreshTokens.Add(token);
            await db.SaveChangesAsync();
        });

        return token.Id;
    }
}
