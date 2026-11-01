using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Conversations;

// The change scope that every membership change runs in: one change to one group at a time, saved with its notifications
// and outbox rows or not at all. The rules themselves are GroupMembersTests; this is what holding them to the database
// means, with real transactions and real concurrent requests.
[Collection(IntegrationTestCollection.Name)]
public class MembershipChangeTests
{
    private readonly ChatApiFactory _factory;

    public MembershipChangeTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(Guid GroupId, TestUser Alice, TestUser Bob, TestUser Carol)> GroupAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);

        return ((await client.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf(), alice, bob, carol);
    }

    private static IConversationRepository Repository(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<IConversationRepository>();

    // Runs one change the way a handler does, in a scope of its own. Returns what the rules refused with, or null when it
    // was saved. The pause after the change has begun lets a second one that is not held back by the lock catch up, so a
    // missing lock shows as two changes made against the same rows.
    private async Task<ChatNETException?> RunAsync(
        Guid groupId, Action<IMembershipChange> rules, TaskCompletionSource? begun = null, Task? otherBegun = null)
    {
        using var scope = _factory.Services.CreateScope();
        await using var change = await Repository(scope).BeginMembershipChangeAsync(groupId, default)
                                 ?? throw new InvalidOperationException("The group does not exist.");

        begun?.TrySetResult();
        if (otherBegun is not null) await Task.WhenAny(otherBegun, Task.Delay(400));

        try
        {
            rules(change);
            await change.CommitAsync([], [], default);
            return null;
        }
        catch (ChatNETException refused)
        {
            return refused;
        }
    }

    private Task<List<ConversationMember>> RowsAsync(Guid groupId)
        => _factory.QueryDbAsync(db => db.ConversationMembers.Where(m => m.ConversationId == groupId).ToListAsync());

    // ##### What a change is #####

    [Fact]
    public async Task There_is_nothing_to_change_in_a_conversation_that_does_not_exist()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.Null(await Repository(scope).BeginMembershipChangeAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task A_change_gives_every_row_of_the_group_and_the_group_itself()
    {
        var (groupId, _, _, _) = await GroupAsync();
        using var scope = _factory.Services.CreateScope();

        await using var change = (await Repository(scope).BeginMembershipChangeAsync(groupId, default))!;

        Assert.Equal(groupId, change.Conversation.Id);
        Assert.Equal(ConversationType.Group, change.Conversation.Type);
        Assert.Equal(3, change.Members.ActiveCount);
    }

    [Fact]
    public async Task Committing_saves_the_rows_the_notification_and_the_outbox_row_together()
    {
        var (groupId, alice, bob, _) = await GroupAsync();
        var now = DateTime.UtcNow;
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(), UserId = bob.Id, ActorId = alice.Id, Type = NotificationType.MemberAdded,
            ConversationId = groupId, CreatedAt = now,
        };
        using var scope = _factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<OutboxPublisher>().Create(new ConversationRead(groupId, bob.Id, now));

        await using (var change = (await Repository(scope).BeginMembershipChangeAsync(groupId, default))!)
        {
            change.Members.Remove(alice.Id, bob.Id, now);
            change.Conversation.Name = "Renamed";
            await change.CommitAsync([notification], [row], default);
        }

        var saved = await _factory.QueryDbAsync(async db => (
            (await db.ConversationMembers.SingleAsync(m => m.ConversationId == groupId && m.UserId == bob.Id)).LeftAt,
            (await db.Conversations.SingleAsync(c => c.Id == groupId)).Name,
            await db.Notifications.AnyAsync(n => n.Id == notification.Id),
            await db.OutboxMessages.AnyAsync(o => o.Id == row.Id)));
        Assert.NotNull(saved.Item1);
        Assert.Equal("Renamed", saved.Item2);
        Assert.True(saved.Item3);
        Assert.True(saved.Item4);
    }

    [Fact]
    public async Task A_change_that_is_not_committed_leaves_the_group_as_it_was()
    {
        var (groupId, alice, bob, _) = await GroupAsync();
        using var scope = _factory.Services.CreateScope();

        await using (var change = (await Repository(scope).BeginMembershipChangeAsync(groupId, default))!)
        {
            change.Members.Remove(alice.Id, bob.Id, DateTime.UtcNow);
        }

        Assert.All(await RowsAsync(groupId), m => Assert.Null(m.LeftAt));
    }

    // ##### One change at a time #####

    [Fact]
    public async Task A_second_change_to_the_same_group_waits_for_the_first_and_then_sees_what_it_did()
    {
        var (groupId, alice, bob, _) = await GroupAsync();
        using var firstScope = _factory.Services.CreateScope();
        using var secondScope = _factory.Services.CreateScope();

        var first = (await Repository(firstScope).BeginMembershipChangeAsync(groupId, default))!;
        first.Members.Remove(alice.Id, bob.Id, DateTime.UtcNow);

        var second = Repository(secondScope).BeginMembershipChangeAsync(groupId, default);
        await Task.Delay(500);
        Assert.False(second.IsCompleted);

        await first.CommitAsync([], [], default);
        await first.DisposeAsync();

        await using var seen = (await second.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.Equal(2, seen.Members.ActiveCount);
    }

    [Fact]
    public async Task Changes_to_different_groups_do_not_wait_for_each_other()
    {
        var (firstId, _, _, _) = await GroupAsync();
        var (secondId, _, _, _) = await GroupAsync();
        using var firstScope = _factory.Services.CreateScope();
        using var secondScope = _factory.Services.CreateScope();

        await using var held = (await Repository(firstScope).BeginMembershipChangeAsync(firstId, default))!;

        var other = Repository(secondScope).BeginMembershipChangeAsync(secondId, default);

        await using var begun = (await other.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.Equal(secondId, begun.Conversation.Id);
    }

    [Fact]
    public async Task A_change_the_rules_refuse_frees_the_group_for_the_next_one()
    {
        var (groupId, _, bob, carol) = await GroupAsync();
        using var firstScope = _factory.Services.CreateScope();
        using var secondScope = _factory.Services.CreateScope();

        await using (var refused = (await Repository(firstScope).BeginMembershipChangeAsync(groupId, default))!)
        {
            Assert.Throws<ForbiddenException>(() => refused.Members.Remove(bob.Id, carol.Id, DateTime.UtcNow));
        }

        await using var next = await Repository(secondScope).BeginMembershipChangeAsync(groupId, default)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(next);
    }

    // ##### Requests that arrive together #####

    [Fact]
    public async Task The_owner_handing_the_group_to_someone_while_removing_them_ends_with_exactly_one_owner()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var (groupId, alice, bob, _) = await GroupAsync();
            var firstBegun = new TaskCompletionSource();
            var secondBegun = new TaskCompletionSource();

            var results = await Task.WhenAll(
                RunAsync(groupId, c => c.Members.Remove(alice.Id, bob.Id, DateTime.UtcNow), firstBegun, secondBegun.Task),
                RunAsync(groupId, c => c.Members.ChangeRole(alice.Id, bob.Id, MemberRole.Owner), secondBegun, firstBegun.Task));

            Assert.Single(results, r => r is null);
            var owners = (await RowsAsync(groupId)).Where(m => m.LeftAt is null && m.Role == MemberRole.Owner).ToList();
            Assert.Single(owners);
        }
    }

    [Fact]
    public async Task The_owner_leaving_while_handing_the_group_over_ends_with_exactly_one_owner()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var (groupId, alice, bob, _) = await GroupAsync();
            var firstBegun = new TaskCompletionSource();
            var secondBegun = new TaskCompletionSource();

            await Task.WhenAll(
                RunAsync(groupId, c => c.Members.Leave(alice.Id, DateTime.UtcNow), firstBegun, secondBegun.Task),
                RunAsync(groupId, c => c.Members.ChangeRole(alice.Id, bob.Id, MemberRole.Owner), secondBegun, firstBegun.Task));

            var owners = (await RowsAsync(groupId)).Where(m => m.LeftAt is null && m.Role == MemberRole.Owner).ToList();
            Assert.Single(owners);
        }
    }

    [Fact]
    public async Task Two_owners_adding_the_same_person_at_once_add_them_once()
    {
        var (groupId, alice, _, _) = await GroupAsync();
        var newcomer = await _factory.RegisterUserAsync("dave");
        var firstBegun = new TaskCompletionSource();
        var secondBegun = new TaskCompletionSource();

        var results = await Task.WhenAll(
            RunAsync(groupId, c => c.Members.Add(alice.Id, [newcomer.Id], DateTime.UtcNow), firstBegun, secondBegun.Task),
            RunAsync(groupId, c => c.Members.Add(alice.Id, [newcomer.Id], DateTime.UtcNow), secondBegun, firstBegun.Task));

        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is ConflictException);
        Assert.Single(await RowsAsync(groupId), m => m.UserId == newcomer.Id);
    }
}
