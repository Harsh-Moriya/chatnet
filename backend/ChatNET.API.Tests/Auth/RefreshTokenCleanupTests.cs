using ChatNET.API.Auth;
using ChatNET.API.Auth.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChatNET.API.Tests.Auth;

// An expired refresh token is useless, so removing old ones only keeps the table from growing for ever.
// They are kept for a week past their expiry, and a token that can still be used is never touched.
[Collection(IntegrationTestCollection.Name)]
public class RefreshTokenCleanupTests
{
    private readonly ChatApiFactory _factory;

    public RefreshTokenCleanupTests(ChatApiFactory factory)
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

    private Task CleanUpAsync() => _factory.Services
        .GetServices<IHostedService>()
        .OfType<RefreshTokenCleanupService>()
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
