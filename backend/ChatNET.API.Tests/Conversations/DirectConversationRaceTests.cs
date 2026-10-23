using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Conversations;

// "Find or create" is only correct if two users starting the same chat at the same moment end up
// in one conversation. The guarantee comes from the unique index on DirectKey; these tests prove
// the application handles losing that race, at the repository and at the HTTP level.
[Collection(IntegrationTestCollection.Name)]
public class DirectConversationRaceTests
{
    private readonly ChatApiFactory _factory;

    public DirectConversationRaceTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_losing_insert_is_reported_not_thrown_and_the_context_stays_usable()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");

        using var winnerScope = _factory.Services.CreateScope();
        using var loserScope = _factory.Services.CreateScope();
        var winner = new ConversationRepository(winnerScope.ServiceProvider.GetRequiredService<AppDbContext>());
        var loserDb = loserScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loser = new ConversationRepository(loserDb);

        var first = await winner.TryAddDirectAsync(NewDirect(alice, bob), CancellationToken.None);
        var second = await loser.TryAddDirectAsync(NewDirect(bob, alice), CancellationToken.None);

        Assert.True(first);
        Assert.False(second);

        // The failed insert must not stay in the change tracker, or the next SaveChanges on this
        // context would try it again and fail again.
        var group = new Conversation
        {
            Type = ConversationType.Group,
            Name = "After the race",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedById = alice.Id,
            Members = [new ConversationMember { UserId = alice.Id, Role = MemberRole.Owner, JoinedAt = DateTime.UtcNow }],
        };
        await loser.AddAsync(group, [], CancellationToken.None);

        var key = DirectKey.For(alice.Id, bob.Id);
        var stored = await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.DirectKey == key));
        Assert.Equal(1, stored);
    }

    // Both requests look up the pair, both find nothing, both insert. The barrier makes that
    // sequence certain instead of merely likely.
    [Fact]
    public async Task Two_requests_that_both_miss_the_lookup_end_up_in_the_same_conversation()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var insertResults = new ConcurrentBag<bool>();
        using var barrier = new Barrier(2);

        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConversationRepository>();
            services.AddScoped<IConversationRepository>(provider =>
                new BarrierConversationRepository(
                    ActivatorUtilities.CreateInstance<ConversationRepository>(provider),
                    barrier,
                    insertResults));
        }));

        using var aliceClient = Authorized(host, alice);
        using var bobClient = Authorized(host, bob);

        var responses = await Task.WhenAll(aliceClient.PostDirectAsync(bob.Id), bobClient.PostDirectAsync(alice.Id));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = new List<Guid>();
        foreach (var response in responses)
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).IdOf());
        Assert.Equal(ids[0], ids[1]);

        // Proves the losing branch really ran: one insert won, one lost.
        Assert.Equal(new[] { false, true }, insertResults.Order().ToArray());

        var key = DirectKey.For(alice.Id, bob.Id);
        var stored = await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.DirectKey == key));
        Assert.Equal(1, stored);
    }

    // No test double here: plain parallel traffic from both sides. Whether or not any request
    // hits the race, the outcome must be one conversation and no errors.
    [Fact]
    public async Task Many_simultaneous_requests_from_both_sides_never_fail_or_duplicate()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        var requests = Enumerable.Range(0, 12)
            .Select(i => i % 2 == 0 ? aliceClient.PostDirectAsync(bob.Id) : bobClient.PostDirectAsync(alice.Id))
            .ToArray();
        var responses = await Task.WhenAll(requests);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).IdOf());
        Assert.Single(ids);

        var key = DirectKey.For(alice.Id, bob.Id);
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.DirectKey == key)));
    }

    private static Conversation NewDirect(TestUser from, TestUser to)
    {
        var now = DateTime.UtcNow;
        return new Conversation
        {
            Type = ConversationType.Direct,
            DirectKey = DirectKey.For(from.Id, to.Id),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedById = from.Id,
            Members =
            [
                new ConversationMember { UserId = from.Id, Role = MemberRole.Member, JoinedAt = now },
                new ConversationMember { UserId = to.Id, Role = MemberRole.Member, JoinedAt = now },
            ],
        };
    }

    private static HttpClient Authorized(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }
}
