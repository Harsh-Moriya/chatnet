using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.Services;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Auth;

// Revoking every session of a person is what a password reset does: every browser and device, whatever family each token is in.
[Collection(IntegrationTestCollection.Name)]
public class RevokeAllRefreshTokensTests
{
    private readonly ChatApiFactory _factory;

    public RevokeAllRefreshTokensTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<Guid> AddTokenAsync(Guid userId, DateTime? revokedAt = null)
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = Guid.NewGuid(),
            TokenHash = TokenHash.Of(Guid.NewGuid().ToString()),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(14),
            RevokedAt = revokedAt,
        };
        await _factory.ExecuteDbAsync(async db =>
        {
            db.RefreshTokens.Add(token);
            await db.SaveChangesAsync();
        });

        return token.Id;
    }

    private Task<DateTime?> RevokedAtAsync(Guid tokenId) =>
        _factory.QueryDbAsync(db => db.RefreshTokens.AsNoTracking().Where(t => t.Id == tokenId).Select(t => t.RevokedAt).SingleAsync());

    [Fact]
    public async Task Every_usable_token_of_the_person_is_revoked_in_every_family_and_the_count_is_returned()
    {
        var user = await _factory.RegisterUserAsync("revokeall");
        var other = await _factory.RegisterUserAsync("revokeallother");
        var firstBrowser = await AddTokenAsync(user.Id);
        var secondBrowser = await AddTokenAsync(user.Id);
        var earlier = DateTime.UtcNow.AddDays(-2);
        var alreadyRevoked = await AddTokenAsync(user.Id, revokedAt: earlier);
        var someoneElses = await AddTokenAsync(other.Id);
        var now = DateTime.UtcNow;

        // The registration of both users also made a session each, which counts.
        var usableBefore = await _factory.QueryDbAsync(db =>
            db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).CountAsync());

        using var scope = _factory.Services.CreateScope();
        var revoked = await scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>()
            .RevokeAllForUserAsync(user.Id, now, CancellationToken.None);

        Assert.Equal(usableBefore, revoked);
        Assert.Equal(now, (await RevokedAtAsync(firstBrowser))!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(now, (await RevokedAtAsync(secondBrowser))!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(earlier, (await RevokedAtAsync(alreadyRevoked))!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(await RevokedAtAsync(someoneElses));
        Assert.Equal(0, await _factory.QueryDbAsync(db =>
            db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).CountAsync()));
    }
}
