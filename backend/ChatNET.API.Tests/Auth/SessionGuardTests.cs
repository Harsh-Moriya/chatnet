using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Auth;

// A password reset ends every session of the person, and it must not miss one that was being created at that very moment: a
// renewal that has retired a token and is about to store its successor, or a sign-in that checked the old password and is about
// to store its first token. The reset locks the person's row; these two paths take a share lock on it, so they take turns with
// the reset instead of running through it.
[Collection(IntegrationTestCollection.Name)]
public class SessionGuardTests
{
    private static readonly TimeSpan LongEnoughToShowItIsWaiting = TimeSpan.FromMilliseconds(700);

    private readonly ChatApiFactory _factory;

    public SessionGuardTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<RefreshToken> AddTokenAsync(Guid userId)
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = Guid.NewGuid(),
            TokenHash = TokenHash.Of(Guid.NewGuid().ToString()),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(14),
        };
        await _factory.ExecuteDbAsync(async db =>
        {
            db.RefreshTokens.Add(token);
            await db.SaveChangesAsync();
        });

        return token;
    }

    private static RefreshToken SuccessorOf(RefreshToken current) => new()
    {
        Id = Guid.NewGuid(),
        UserId = current.UserId,
        FamilyId = current.FamilyId,
        TokenHash = TokenHash.Of(Guid.NewGuid().ToString()),
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(14),
    };

    private Task<bool> RotateAsync(RefreshToken current, RefreshToken successor) => Task.Run(async () =>
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>()
            .TryRotateAsync(current, successor, DateTime.UtcNow, CancellationToken.None);
    });

    private Task<AppUser> LoadAsync(Guid userId) =>
        _factory.QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId));

    private Task<IssuedRefreshToken> StartSessionAsync(AppUser user) => Task.Run(async () =>
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RefreshTokenIssuer>().StartSessionAsync(user, CancellationToken.None);
    });

    private async Task<int> FamiliesOfAsync(Guid userId) => await _factory.QueryDbAsync(db =>
        db.RefreshTokens.Where(t => t.UserId == userId).Select(t => t.FamilyId).Distinct().CountAsync());

    [Fact]
    public async Task A_renewal_started_while_a_reset_holds_the_person_waits_and_then_creates_nothing()
    {
        var user = await _factory.RegisterUserAsync("rotaterace");
        var current = await AddTokenAsync(user.Id);
        var successor = SuccessorOf(current);
        await using var reset = await _factory.HoldUserRowAsync(user.Id);

        var rotation = RotateAsync(current, successor);
        await Task.Delay(LongEnoughToShowItIsWaiting);
        Assert.False(rotation.IsCompleted, "The renewal went ahead while the reset held the person's row.");

        await reset.ExecuteAsync("UPDATE \"RefreshTokens\" SET \"RevokedAt\" = now() WHERE \"UserId\" = @id", ("id", user.Id));
        await reset.CommitAsync();

        Assert.False(await rotation);
        var stored = await _factory.QueryDbAsync(db => db.RefreshTokens.AsNoTracking().Where(t => t.Id == successor.Id).CountAsync());
        Assert.Equal(0, stored);
    }

    [Fact]
    public async Task A_renewal_with_nothing_holding_the_person_goes_ahead()
    {
        var user = await _factory.RegisterUserAsync("rotatefree");
        var current = await AddTokenAsync(user.Id);
        var successor = SuccessorOf(current);

        Assert.True(await RotateAsync(current, successor));
    }

    [Fact]
    public async Task A_sign_in_whose_stamp_is_still_current_stores_its_first_token()
    {
        var user = await _factory.RegisterUserAsync("stampsame");
        var loaded = await LoadAsync(user.Id);
        var familiesBefore = await FamiliesOfAsync(user.Id);

        await StartSessionAsync(loaded);

        Assert.Equal(familiesBefore + 1, await FamiliesOfAsync(user.Id));
    }

    [Fact]
    public async Task A_sign_in_that_checked_the_password_before_it_changed_is_refused_and_stores_nothing()
    {
        var user = await _factory.RegisterUserAsync("stampchanged");
        var loaded = await LoadAsync(user.Id);
        await _factory.ExecuteDbAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString())));
        var familiesBefore = await FamiliesOfAsync(user.Id);

        var refused = await Assert.ThrowsAsync<UnauthorizedException>(() => StartSessionAsync(loaded));

        Assert.Equal("Invalid email or password.", refused.Message);
        Assert.Equal(familiesBefore, await FamiliesOfAsync(user.Id));
    }

    [Fact]
    public async Task A_sign_in_started_while_a_reset_holds_the_person_waits_and_is_refused_when_the_stamp_changed()
    {
        var user = await _factory.RegisterUserAsync("stamprace");
        var loaded = await LoadAsync(user.Id);
        var familiesBefore = await FamiliesOfAsync(user.Id);
        await using var reset = await _factory.HoldUserRowAsync(user.Id);

        var signIn = StartSessionAsync(loaded);
        await Task.Delay(LongEnoughToShowItIsWaiting);
        Assert.False(signIn.IsCompleted, "The sign-in went ahead while the reset held the person's row.");

        await reset.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"SecurityStamp\" = @stamp WHERE \"Id\" = @id",
            ("stamp", Guid.NewGuid().ToString()), ("id", user.Id));
        await reset.CommitAsync();

        await Assert.ThrowsAsync<UnauthorizedException>(() => signIn);
        Assert.Equal(familiesBefore, await FamiliesOfAsync(user.Id));
    }

    [Fact]
    public async Task A_sign_in_for_a_person_who_no_longer_exists_is_refused()
    {
        var user = await _factory.RegisterUserAsync("stampgone");
        var loaded = await LoadAsync(user.Id);
        await _factory.ExecuteDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync());

        await Assert.ThrowsAsync<UnauthorizedException>(() => StartSessionAsync(loaded));
    }
}
