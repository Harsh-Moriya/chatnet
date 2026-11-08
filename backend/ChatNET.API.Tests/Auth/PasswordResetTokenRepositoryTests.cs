using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Auth;

// A reset link is single use and short lived, and the rules live in one conditional statement: it marks the link used and
// hands back the person only if the link is unused and not yet expired, so of any number of requests with the same link
// exactly one succeeds. The tests call the repository, as a handler does, one scope per call.
[Collection(IntegrationTestCollection.Name)]
public class PasswordResetTokenRepositoryTests
{
    private readonly ChatApiFactory _factory;

    public PasswordResetTokenRepositoryTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<T> WithRepositoryAsync<T>(Func<IPasswordResetTokenRepository, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IPasswordResetTokenRepository>());
    }

    private Task<Guid?> ConsumeAsync(string token, DateTime now) =>
        WithRepositoryAsync(repository => repository.TryConsumeAsync(TokenHash.Of(token), now, CancellationToken.None));

    private static PasswordResetToken RowFor(Guid userId, string token) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = TokenHash.Of(token),
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddMinutes(30),
    };

    [Fact]
    public async Task A_link_that_can_be_used_returns_its_person_and_is_marked_used_at_that_moment()
    {
        var user = await _factory.RegisterUserAsync("consume");
        var token = await _factory.AddResetTokenAsync(user.Id);
        var now = DateTime.UtcNow;

        var consumed = await ConsumeAsync(token, now);

        Assert.Equal(user.Id, consumed);
        var row = await _factory.ResetTokenAsync(token);
        Assert.Equal(now, row!.UsedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task The_owner_of_a_link_is_found_whether_or_not_it_can_still_be_used_and_nobody_owns_an_unknown_one()
    {
        var user = await _factory.RegisterUserAsync("owner");
        var waiting = await _factory.AddResetTokenAsync(user.Id);
        var used = await _factory.AddResetTokenAsync(user.Id, usedAt: DateTime.UtcNow.AddMinutes(-1));
        var expired = await _factory.AddResetTokenAsync(user.Id, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(user.Id, await FindOwnerAsync(waiting));
        Assert.Equal(user.Id, await FindOwnerAsync(used));
        Assert.Equal(user.Id, await FindOwnerAsync(expired));
        Assert.Null(await FindOwnerAsync("not-a-token-that-was-ever-issued"));

        // Looking does not use the link up.
        Assert.Null((await _factory.ResetTokenAsync(waiting))!.UsedAt);
    }

    private Task<Guid?> FindOwnerAsync(string token) =>
        WithRepositoryAsync(repository => repository.FindUserIdAsync(TokenHash.Of(token), CancellationToken.None));

    [Fact]
    public async Task A_link_cannot_be_used_twice()
    {
        var user = await _factory.RegisterUserAsync("twice");
        var token = await _factory.AddResetTokenAsync(user.Id);

        Assert.Equal(user.Id, await ConsumeAsync(token, DateTime.UtcNow));
        Assert.Null(await ConsumeAsync(token, DateTime.UtcNow));
    }

    [Fact]
    public async Task A_link_that_was_voided_is_refused()
    {
        var user = await _factory.RegisterUserAsync("voided");
        var token = await _factory.AddResetTokenAsync(user.Id, usedAt: DateTime.UtcNow.AddMinutes(-1));

        Assert.Null(await ConsumeAsync(token, DateTime.UtcNow));
    }

    [Fact]
    public async Task A_token_nobody_issued_is_refused()
    {
        Assert.Null(await ConsumeAsync("not-a-token-that-was-ever-issued", DateTime.UtcNow));
    }

    // The link is usable while the time is before its expiry: at the expiry itself it is no longer.
    [Fact]
    public async Task A_link_is_usable_up_to_but_not_including_its_expiry()
    {
        var user = await _factory.RegisterUserAsync("expiry");
        var expiresAt = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var justBefore = await _factory.AddResetTokenAsync(user.Id, expiresAt);
        var exactly = await _factory.AddResetTokenAsync(user.Id, expiresAt);
        var after = await _factory.AddResetTokenAsync(user.Id, expiresAt);

        Assert.Equal(user.Id, await ConsumeAsync(justBefore, expiresAt.AddTicks(-10)));
        Assert.Null(await ConsumeAsync(exactly, expiresAt));
        Assert.Null(await ConsumeAsync(after, expiresAt.AddSeconds(1)));
    }

    [Fact]
    public async Task Of_many_requests_with_the_same_link_at_the_same_moment_exactly_one_wins()
    {
        var user = await _factory.RegisterUserAsync("race");
        var token = await _factory.AddResetTokenAsync(user.Id);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ConsumeAsync(token, DateTime.UtcNow))));

        Assert.Equal(1, results.Count(person => person == user.Id));
        Assert.Equal(19, results.Count(person => person is null));
    }

    [Fact]
    public async Task Voiding_marks_only_the_unused_links_of_that_person()
    {
        var user = await _factory.RegisterUserAsync("void");
        var other = await _factory.RegisterUserAsync("voidother");
        var waiting1 = await _factory.AddResetTokenAsync(user.Id);
        var waiting2 = await _factory.AddResetTokenAsync(user.Id);
        var alreadyUsedAt = DateTime.UtcNow.AddHours(-2);
        var used = await _factory.AddResetTokenAsync(user.Id, usedAt: alreadyUsedAt);
        var someoneElses = await _factory.AddResetTokenAsync(other.Id);
        var now = DateTime.UtcNow;

        await WithRepositoryAsync(async repository =>
        {
            await repository.VoidUnusedAsync(user.Id, now, CancellationToken.None);
            return 0;
        });

        Assert.Equal(now, (await _factory.ResetTokenAsync(waiting1))!.UsedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(now, (await _factory.ResetTokenAsync(waiting2))!.UsedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(alreadyUsedAt, (await _factory.ResetTokenAsync(used))!.UsedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null((await _factory.ResetTokenAsync(someoneElses))!.UsedAt);
    }

    [Fact]
    public async Task Expired_links_are_deleted_only_when_they_expired_before_the_cutoff()
    {
        var user = await _factory.RegisterUserAsync("delete");
        var cutoff = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = await _factory.AddResetTokenAsync(user.Id, cutoff.AddSeconds(-1));
        var exactly = await _factory.AddResetTokenAsync(user.Id, cutoff);
        var newer = await _factory.AddResetTokenAsync(user.Id, cutoff.AddDays(1));

        await WithRepositoryAsync(repository => repository.DeleteExpiredBeforeAsync(cutoff, CancellationToken.None));

        Assert.Null(await _factory.ResetTokenAsync(older));
        Assert.NotNull(await _factory.ResetTokenAsync(exactly));
        Assert.NotNull(await _factory.ResetTokenAsync(newer));
    }

    [Fact]
    public async Task Deleting_the_person_deletes_their_links()
    {
        var user = await _factory.RegisterUserAsync("cascade");
        var token = await _factory.AddResetTokenAsync(user.Id);

        await _factory.ExecuteDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync());

        Assert.Null(await _factory.ResetTokenAsync(token));
    }

    [Fact]
    public async Task Two_links_with_the_same_hash_cannot_be_stored()
    {
        var user = await _factory.RegisterUserAsync("unique");
        var token = await _factory.AddResetTokenAsync(user.Id);

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenRepository>();

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(RowFor(user.Id, token), CancellationToken.None));
    }

    // The outbox relay handles a pass with one context. A failed insert that stayed tracked would be tried again by every later
    // save of that context and fail each time, so no other row of the pass could be saved.
    [Fact]
    public async Task A_failed_insert_leaves_nothing_tracked_so_the_next_save_on_the_same_context_works()
    {
        var user = await _factory.RegisterUserAsync("tracked");
        var token = await _factory.AddResetTokenAsync(user.Id);

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenRepository>();
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(RowFor(user.Id, token), CancellationToken.None));

        await repository.AddAsync(RowFor(user.Id, "a-different-token"), CancellationToken.None);

        Assert.NotNull(await _factory.ResetTokenAsync("a-different-token"));
    }
    // Raw SQL, so its cost is pinned: finding the link has to go through the unique index on the hash, however many links the
    // table holds. The statement is explained after it ran once, so it finds nothing to change, but its plan is the same.
    [Fact]
    public async Task Using_a_link_reads_one_row_however_many_links_are_stored()
    {
        var user = await _factory.RegisterUserAsync("plancost");
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        var tag = DateTime.UtcNow.AddYears(40);
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PasswordResetTokens" ("Id", "UserId", "TokenHash", "CreatedAt", "ExpiresAt", "UsedAt")
            SELECT gen_random_uuid(), {user.Id}, encode(sha256(g::text::bytea), 'hex'), now(), {tag}, NULL
            FROM generate_series(1, 20000) AS g
            """));
        var token = await _factory.AddResetTokenAsync(user.Id);

        // Without statistics the planner knows nothing about a table that was just filled, and the plan means nothing.
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlRawAsync(@"ANALYZE ""PasswordResetTokens"""));

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                recorder.Clear();
                var consumed = await scope.ServiceProvider.GetRequiredService<IPasswordResetTokenRepository>()
                    .TryConsumeAsync(TokenHash.Of(token), DateTime.UtcNow, CancellationToken.None);
                Assert.Equal(user.Id, consumed);
            }

            var statement = Assert.Single(recorder.Commands, c => c.Sql.Contains("\"PasswordResetTokens\""));
            var rowsRead = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, statement, "PasswordResetTokens"));

            Assert.True(rowsRead <= 5, $"Using one link read {rowsRead} PasswordResetTokens rows among 20,001.");
        }
        finally
        {
            await _factory.ExecuteDbAsync(db => db.PasswordResetTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync());
        }
    }
}
