using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Conversations;

// How the keys of a conversation follow its life: the first key period is made with the conversation, a new one with the people
// who join, and a removal or a leave marks the current one for replacement. In every case the epoch is saved with the change that
// needs it, or neither is saved.
[Collection(IntegrationTestCollection.Name)]
public class ConversationKeysTests
{
    private readonly ChatApiFactory _factory;

    public ConversationKeysTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient As(TestUser user) => _factory.CreateClientFor(user);

    private Task<Conversation> ConversationAsync(Guid id) =>
        _factory.QueryDbAsync(db => db.Conversations.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<List<ConversationKeyEpoch>> EpochsAsync(Guid id) =>
        _factory.QueryDbAsync(db => db.ConversationKeyEpochs.AsNoTracking().Include(e => e.Envelopes).Where(e => e.ConversationId == id)
            .OrderBy(e => e.Epoch).ToListAsync());

    // The rule everything below keeps: whenever nothing is waiting to be replaced, the newest epoch was made for exactly the people
    // who are in the conversation.
    private async Task AssertCurrentEpochIsForTheMembersAsync(Guid id)
    {
        var conversation = await ConversationAsync(id);
        Assert.False(conversation.KeyRotationRequired);
        var members = await _factory.QueryDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == id && m.LeftAt == null).Select(m => m.UserId).ToListAsync());
        var newest = (await EpochsAsync(id)).Single(e => e.Epoch == conversation.KeyEpoch);
        Assert.Equal(members.Order(), newest.Envelopes.Select(e => e.UserId).Order());
    }

    private async Task<(TestUser Owner, TestUser Member, TestUser Other, TestUser Outsider, Guid Id)> GroupAsync()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var other = await _factory.RegisterUserAsync("other");
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = As(owner);
        var group = await client.CreateGroupAsync("Team", member.Id, other.Id);
        return (owner, member, other, outsider, group.IdOf());
    }

    // ##### A direct conversation #####

    [Fact]
    public async Task A_new_direct_conversation_gets_its_first_epoch_made_for_both_people_and_the_id_the_browser_chose()
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        using var client = As(ana);
        var id = Guid.NewGuid();
        var body = EpochBody.For(1, ana.Id, ben.Id);

        var response = await client.PostAsJsonAsync("/api/conversations/direct", new { id, otherUserId = ben.Id, epoch = body });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.ReadJsonAsync();
        Assert.Equal(id, dto.GetProperty("id").GetGuid());
        Assert.Equal(1, dto.GetProperty("keyEpoch").GetInt32());
        Assert.False(dto.GetProperty("keyRotationRequired").GetBoolean());
        var epoch = Assert.Single(await EpochsAsync(id));
        Assert.Equal(KeyEpochReason.Created, epoch.Reason);
        Assert.Equal(ana.Id, epoch.CreatedById);
        Assert.Equal(body.CommitmentBytes, epoch.KeyCommitment);
        Assert.Equal(body.SignatureBytes, epoch.Signature);
        Assert.Equal(body.EnvelopeOf(ben.Id), epoch.Envelopes.Single(e => e.UserId == ben.Id).Envelope);
        await AssertCurrentEpochIsForTheMembersAsync(id);
    }

    [Fact]
    public async Task Asking_for_a_direct_conversation_that_exists_returns_it_and_ignores_the_epoch_offered()
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        using var anaClient = As(ana);
        using var benClient = As(ben);
        var first = await anaClient.CreateDirectAsync(ben.Id);

        var again = await benClient.CreateDirectAsync(ana.Id);
        var oddEpoch = await benClient.PostAsJsonAsync("/api/conversations/direct", new
        {
            id = Guid.NewGuid(),
            otherUserId = ana.Id,
            epoch = EpochBody.For(5, Guid.NewGuid()),
        });

        Assert.Equal(first.IdOf(), again.IdOf());
        Assert.Equal(HttpStatusCode.OK, oddEpoch.StatusCode);
        Assert.Equal(first.IdOf(), (await oddEpoch.ReadJsonAsync()).IdOf());
        Assert.Single(await EpochsAsync(first.IdOf()));
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.DirectKey != null && c.Members.Any(m => m.UserId == ana.Id) && c.Members.Any(m => m.UserId == ben.Id))));
    }

    [Fact]
    public async Task A_direct_conversation_with_someone_who_has_no_keys_is_refused_and_nothing_is_made()
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var keyless = await _factory.RegisterUserWithoutKeysAsync("keyless");
        using var client = As(ana);
        var id = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/conversations/direct", new
        {
            id,
            otherUserId = keyless.Id,
            epoch = EpochBody.For(1, ana.Id, keyless.Id),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.MemberHasNoKeys, (await response.ReadProblemAsync()).Code);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.Id == id)));
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task A_first_epoch_that_is_not_for_exactly_both_people_is_refused()
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        var cy = await _factory.RegisterUserAsync("cy");
        using var client = As(ana);

        foreach (var wrong in new[] { EpochBody.For(1, ana.Id), EpochBody.For(1, ben.Id), EpochBody.For(1, ana.Id, ben.Id, cy.Id), EpochBody.For(1, ana.Id, cy.Id) })
        {
            var id = Guid.NewGuid();
            var response = await client.PostAsJsonAsync("/api/conversations/direct", new { id, otherUserId = ben.Id, epoch = wrong });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(KeyEpochCodes.MembersChanged, (await response.ReadProblemAsync()).Code);
            Assert.Equal(0, await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.Id == id)));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_first_epoch_must_be_number_one(int number)
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        using var client = As(ana);

        var response = await client.PostAsJsonAsync("/api/conversations/direct", new
        {
            id = Guid.NewGuid(),
            otherUserId = ben.Id,
            epoch = EpochBody.For(number, ana.Id, ben.Id),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.Stale, (await response.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task A_first_epoch_sealed_to_an_old_key_generation_is_refused()
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountKeyBundles" ("UserId", "Generation", "Bundle", "CreatedAt")
            VALUES ({ben.Id}, 2, {EncryptionApi.Bundle(generation: 2)}, now())
            """));
        using var client = As(ana);

        var response = await client.PostAsJsonAsync("/api/conversations/direct", new
        {
            id = Guid.NewGuid(),
            otherUserId = ben.Id,
            epoch = EpochBody.For(1, ana.Id, ben.Id),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.MembersChanged, (await response.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Two_people_starting_the_same_direct_conversation_together_leave_one_conversation_with_one_epoch()
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        using var anaClient = As(ana);
        using var benClient = As(ben);
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

        var responses = await Task.WhenAll(ids.Select((id, i) => (i % 2 == 0 ? anaClient : benClient).PostDirectAsync(i % 2 == 0 ? ben.Id : ana.Id, id)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var winners = (await Task.WhenAll(responses.Select(r => r.ReadJsonAsync()))).Select(c => c.IdOf()).Distinct().ToList();
        var winner = Assert.Single(winners);
        Assert.Single(await EpochsAsync(winner));
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.ConversationKeyEpochs.CountAsync(e => ids.Contains(e.ConversationId))));
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => ids.Contains(c.Id))));
    }

    [Fact]
    public async Task An_id_that_is_already_a_conversations_is_refused_and_changes_nothing()
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        var cy = await _factory.RegisterUserAsync("cy");
        using var client = As(ana);
        var first = await client.PostDirectAsync(ben.Id);
        var taken = (await first.ReadJsonAsync()).IdOf();

        var direct = await client.PostDirectAsync(cy.Id, taken);
        var group = await client.PostAsJsonAsync("/api/conversations/group", new
        {
            id = taken,
            name = "Team",
            memberIds = new[] { cy.Id },
            epoch = EpochBody.For(1, ana.Id, cy.Id),
        });

        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, group.StatusCode);
        Assert.Single(await EpochsAsync(taken));
        Assert.Equal(ben.Id, (await _factory.QueryDbAsync(db => db.ConversationMembers.Where(m => m.ConversationId == taken && m.UserId != ana.Id).ToListAsync())).Single().UserId);
    }

    // ##### A new group #####

    [Fact]
    public async Task A_new_group_gets_its_first_epoch_made_for_its_creator_and_every_member()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var other = await _factory.RegisterUserAsync("other");
        using var client = As(owner);
        var id = Guid.NewGuid();
        var body = EpochBody.For(1, owner.Id, member.Id, other.Id);

        var response = await client.PostAsJsonAsync("/api/conversations/group", new
        {
            id,
            name = "Team",
            memberIds = new[] { member.Id, other.Id },
            epoch = body,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.ReadJsonAsync();
        Assert.Equal(id, dto.GetProperty("id").GetGuid());
        Assert.Equal(1, dto.GetProperty("keyEpoch").GetInt32());
        var epoch = Assert.Single(await EpochsAsync(id));
        Assert.Equal(KeyEpochReason.Created, epoch.Reason);
        Assert.Equal(owner.Id, epoch.CreatedById);
        Assert.Equal(3, epoch.Envelopes.Count);
        Assert.Equal(body.EnvelopeOf(other.Id), epoch.Envelopes.Single(e => e.UserId == other.Id).Envelope);
        await AssertCurrentEpochIsForTheMembersAsync(id);
    }

    [Fact]
    public async Task The_creator_listed_among_the_members_is_still_one_envelope()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        using var client = As(owner);

        var response = await client.PostGroupAsync("Team", owner.Id, member.Id, member.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, (await EpochsAsync((await response.ReadJsonAsync()).IdOf())).Single().Envelopes.Count);
    }

    [Fact]
    public async Task A_group_with_someone_who_has_no_keys_is_refused_and_no_group_notification_or_push_is_left()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var keyless = await _factory.RegisterUserWithoutKeysAsync("keyless");
        using var client = As(owner);
        var id = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/conversations/group", new
        {
            id,
            name = "Team",
            memberIds = new[] { member.Id, keyless.Id },
            epoch = EpochBody.For(1, owner.Id, member.Id, keyless.Id),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.MemberHasNoKeys, (await response.ReadProblemAsync()).Code);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.Id == id)));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Notifications.CountAsync(n => n.ConversationId == id)));
        Assert.Empty(await _factory.OutboxRowsAboutAsync("conversationId", id));
    }

    [Fact]
    public async Task A_group_epoch_that_leaves_someone_out_or_adds_a_stranger_is_refused()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = As(owner);

        foreach (var wrong in new[] { EpochBody.For(1, owner.Id), EpochBody.For(1, member.Id), EpochBody.For(1, owner.Id, member.Id, stranger.Id) })
        {
            var id = Guid.NewGuid();
            var response = await client.PostAsJsonAsync("/api/conversations/group", new
            {
                id, name = "Team", memberIds = new[] { member.Id }, epoch = wrong,
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(0, await _factory.QueryDbAsync(db => db.Conversations.CountAsync(c => c.Id == id)));
        }
    }

    [Fact]
    public async Task A_person_who_does_not_exist_is_reported_before_the_epoch_is_looked_at()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        using var client = As(owner);
        var ghost = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/conversations/group", new
        {
            id = Guid.NewGuid(), name = "Team", memberIds = new[] { ghost }, epoch = EpochBody.For(1, owner.Id),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static TheoryData<string, string> BadCreateBodies => new()
    {
        { "direct without an id", "direct|{\"otherUserId\":\"OTHER\",\"epoch\":EPOCH}" },
        { "direct with an empty id", "direct|{\"id\":\"00000000-0000-0000-0000-000000000000\",\"otherUserId\":\"OTHER\",\"epoch\":EPOCH}" },
        { "direct without an epoch", "direct|{\"id\":\"ID\",\"otherUserId\":\"OTHER\"}" },
        { "group without an id", "group|{\"name\":\"Team\",\"memberIds\":[\"OTHER\"],\"epoch\":EPOCH}" },
        { "group with an empty id", "group|{\"id\":\"00000000-0000-0000-0000-000000000000\",\"name\":\"Team\",\"memberIds\":[\"OTHER\"],\"epoch\":EPOCH}" },
        { "group without an epoch", "group|{\"id\":\"ID\",\"name\":\"Team\",\"memberIds\":[\"OTHER\"]}" },
    };

    [Theory]
    [MemberData(nameof(BadCreateBodies))]
    public async Task A_conversation_without_its_id_or_first_epoch_is_refused(string _, string spec)
    {
        var ana = await _factory.RegisterUserAsync("ana");
        var ben = await _factory.RegisterUserAsync("ben");
        using var client = As(ana);
        var (kind, template) = (spec.Split('|')[0], spec.Split('|')[1]);
        var json = template
            .Replace("OTHER", ben.Id.ToString())
            .Replace("ID", Guid.NewGuid().ToString())
            .Replace("EPOCH", JsonSerializer.Serialize(EpochBody.For(1, ana.Id, ben.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var response = await client.PostAsync($"/api/conversations/{kind}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await client.ListConversationsAsync());
    }

    // ##### Adding people #####

    [Fact]
    public async Task Adding_people_stores_a_new_epoch_for_everyone_who_will_be_in_the_group_with_them()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        var before = (await ConversationAsync(team.Id)).KeyEpoch;

        var people = await owner.AddMembersAsync(team.Id, team.Outsider.Id);

        Assert.Equal(4, people.Count);
        var conversation = await ConversationAsync(team.Id);
        Assert.Equal(before + 1, conversation.KeyEpoch);
        var epochs = await EpochsAsync(team.Id);
        Assert.Equal(KeyEpochReason.MemberAdded, epochs.Last().Reason);
        Assert.Equal(team.Owner.Id, epochs.Last().CreatedById);
        Assert.Contains(epochs.Last().Envelopes, e => e.UserId == team.Outsider.Id);
        await AssertCurrentEpochIsForTheMembersAsync(team.Id);
    }

    [Fact]
    public async Task A_newcomer_holds_no_envelope_for_what_came_before_them()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        await owner.AddMembersAsync(team.Id, team.Outsider.Id);

        var rows = await _factory.QueryDbAsync(db => db.ConversationKeyEnvelopes
            .Where(v => v.ConversationId == team.Id && v.UserId == team.Outsider.Id).Select(v => v.Epoch).ToListAsync());

        Assert.Equal([(await ConversationAsync(team.Id)).KeyEpoch], rows);
    }

    [Fact]
    public async Task Adding_people_with_an_epoch_that_leaves_out_the_newcomer_or_a_member_adds_nobody()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        var before = await ConversationAsync(team.Id);
        var everyone = new[] { team.Owner.Id, team.Member.Id, team.Other.Id };

        foreach (var wrong in new[]
                 {
                     EpochBody.For(before.KeyEpoch + 1, everyone),
                     EpochBody.For(before.KeyEpoch + 1, team.Owner.Id, team.Member.Id, team.Outsider.Id),
                     EpochBody.For(before.KeyEpoch + 1, [.. everyone, team.Outsider.Id, Guid.NewGuid()]),
                 })
        {
            var response = await owner.PostAsJsonAsync($"/api/conversations/{team.Id}/members", new { userIds = new[] { team.Outsider.Id }, epoch = wrong });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(KeyEpochCodes.MembersChanged, (await response.ReadProblemAsync()).Code);
        }

        var after = await ConversationAsync(team.Id);
        Assert.Equal(before.KeyEpoch, after.KeyEpoch);
        Assert.Equal(before.KeyEpoch, (await EpochsAsync(team.Id)).Count);
        Assert.Equal(3, (await owner.GetMembersAsync(team.Id)).Count);
    }

    [Fact]
    public async Task Adding_people_with_an_epoch_that_is_not_the_next_one_is_stale_and_adds_nobody()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        var current = (await ConversationAsync(team.Id)).KeyEpoch;

        var response = await owner.PostAsJsonAsync($"/api/conversations/{team.Id}/members", new
        {
            userIds = new[] { team.Outsider.Id },
            epoch = EpochBody.For(current, team.Owner.Id, team.Member.Id, team.Other.Id, team.Outsider.Id),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.Stale, (await response.ReadProblemAsync()).Code);
        Assert.Equal(3, (await owner.GetMembersAsync(team.Id)).Count);
    }

    [Fact]
    public async Task Adding_someone_who_has_no_keys_is_refused_and_adds_nobody()
    {
        var team = await GroupAsync();
        var keyless = await _factory.RegisterUserWithoutKeysAsync("keyless");
        using var owner = As(team.Owner);
        var current = (await ConversationAsync(team.Id)).KeyEpoch;

        var response = await owner.PostAsJsonAsync($"/api/conversations/{team.Id}/members", new
        {
            userIds = new[] { keyless.Id },
            epoch = EpochBody.For(current + 1, team.Owner.Id, team.Member.Id, team.Other.Id, keyless.Id),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.MemberHasNoKeys, (await response.ReadProblemAsync()).Code);
        Assert.Equal(3, (await owner.GetMembersAsync(team.Id)).Count);
        Assert.DoesNotContain(await owner.GetMembersAsync(team.Id), m => m.GetProperty("userId").GetGuid() == keyless.Id);
    }

    [Fact]
    public async Task Adding_people_to_a_group_that_had_no_epoch_gives_it_its_first()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var member = await _factory.RegisterUserAsync("member");
        var newcomer = await _factory.RegisterUserAsync("newcomer");
        var id = await _factory.AddLegacyGroupAsync(owner.Id, "Old team", member.Id);
        using var client = As(owner);

        await client.AddMembersAsync(id, newcomer.Id);

        var conversation = await ConversationAsync(id);
        Assert.Equal(1, conversation.KeyEpoch);
        Assert.Equal(KeyEpochReason.MemberAdded, (await EpochsAsync(id)).Single().Reason);
        await AssertCurrentEpochIsForTheMembersAsync(id);
    }

    [Fact]
    public async Task Someone_who_comes_back_is_given_a_new_epoch_too()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        using var other = As(team.Other);
        await other.LeaveResponseAsync(team.Id);
        var whileAway = (await ConversationAsync(team.Id)).KeyEpoch;

        await owner.AddMembersAsync(team.Id, team.Other.Id);

        Assert.Equal(whileAway + 1, (await ConversationAsync(team.Id)).KeyEpoch);
        await AssertCurrentEpochIsForTheMembersAsync(team.Id);
    }

    [Fact]
    public async Task Two_managers_adding_different_people_together_leave_a_group_whose_epoch_matches_its_people()
    {
        var team = await GroupAsync();
        var first = await _factory.RegisterUserAsync("first");
        var second = await _factory.RegisterUserAsync("second");
        using var owner = As(team.Owner);
        await owner.ChangeRoleResponseAsync(team.Id, team.Member.Id, "Admin");
        using var admin = As(team.Member);
        var current = (await ConversationAsync(team.Id)).KeyEpoch;
        var present = new[] { team.Owner.Id, team.Member.Id, team.Other.Id };

        var responses = await Task.WhenAll(
            owner.PostAsJsonAsync($"/api/conversations/{team.Id}/members", new { userIds = new[] { first.Id }, epoch = EpochBody.For(current + 1, [.. present, first.Id]) }),
            admin.PostAsJsonAsync($"/api/conversations/{team.Id}/members", new { userIds = new[] { second.Id }, epoch = EpochBody.For(current + 1, [.. present, second.Id]) }));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(4, (await owner.GetMembersAsync(team.Id)).Count);
        Assert.Equal(current + 1, (await ConversationAsync(team.Id)).KeyEpoch);
        await AssertCurrentEpochIsForTheMembersAsync(team.Id);
    }

    [Fact]
    public async Task Adding_people_tells_the_members_which_epoch_is_newest()
    {
        var team = await GroupAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, team.Outsider);
        using var owner = As(team.Owner);

        await owner.AddMembersAsync(team.Id, team.Outsider.Id);

        var expected = (await ConversationAsync(team.Id)).KeyEpoch;
        var state = await memberTab.WaitForKeyChangedAsync(team.Id);
        Assert.Equal(expected, state.GetProperty("keyEpoch").GetInt32());
        Assert.False(state.GetProperty("keyRotationRequired").GetBoolean());
        // The newcomer is told as a member of the group they were added to.
        Assert.Equal(expected, (await outsiderTab.WaitForKeyChangedAsync(team.Id)).GetProperty("keyEpoch").GetInt32());
    }

    // ##### Removing and leaving #####

    [Fact]
    public async Task Removing_a_member_flags_the_current_epoch_for_replacement_and_changes_nothing_else_about_it()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        var before = await ConversationAsync(team.Id);

        var response = await owner.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await ConversationAsync(team.Id);
        Assert.True(after.KeyRotationRequired);
        Assert.Equal(before.KeyEpoch, after.KeyEpoch);
        Assert.Equal(before.KeyEpoch, (await EpochsAsync(team.Id)).Count);
    }

    [Fact]
    public async Task Leaving_a_group_flags_the_current_epoch_for_replacement()
    {
        var team = await GroupAsync();
        using var member = As(team.Member);

        var response = await member.LeaveResponseAsync(team.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ConversationAsync(team.Id)).KeyRotationRequired);
    }

    [Fact]
    public async Task A_removal_that_is_refused_flags_nothing()
    {
        var team = await GroupAsync();
        using var member = As(team.Member);

        var response = await member.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await ConversationAsync(team.Id)).KeyRotationRequired);
    }

    [Fact]
    public async Task The_next_epoch_after_a_removal_clears_the_flag_and_cannot_include_the_person_removed()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        await owner.RemoveMemberResponseAsync(team.Id, team.Other.Id);
        var current = (await ConversationAsync(team.Id)).KeyEpoch;

        var withThem = await owner.PostEpochAsync(team.Id, EpochBody.For(current + 1, team.Owner.Id, team.Member.Id, team.Other.Id));
        var without = await owner.PostEpochAsync(team.Id, EpochBody.For(current + 1, team.Owner.Id, team.Member.Id));

        Assert.Equal(HttpStatusCode.Conflict, withThem.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, without.StatusCode);
        await AssertCurrentEpochIsForTheMembersAsync(team.Id);
    }

    [Fact]
    public async Task Adding_someone_after_a_removal_replaces_the_epoch_and_clears_the_flag()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);
        await owner.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        await owner.AddMembersAsync(team.Id, team.Outsider.Id);

        await AssertCurrentEpochIsForTheMembersAsync(team.Id);
    }

    [Fact]
    public async Task A_removal_tells_the_people_who_remain_but_not_the_person_removed()
    {
        var team = await GroupAsync();
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var removedTab = await HubClient.ConnectAsync(_factory, team.Other);
        using var owner = As(team.Owner);
        var epoch = (await ConversationAsync(team.Id)).KeyEpoch;

        await owner.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        var state = await memberTab.WaitForKeyChangedAsync(team.Id, s => s.GetProperty("keyRotationRequired").GetBoolean());
        Assert.Equal(epoch, state.GetProperty("keyEpoch").GetInt32());
        Assert.False(await removedTab.ReceivedKeyChangedAsync(team.Id));
    }

    [Fact]
    public async Task Every_push_about_a_removal_is_made_by_the_request_and_marked_done_at_once()
    {
        var team = await GroupAsync();
        using var owner = As(team.Owner);

        await owner.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        var rows = (await _factory.OutboxRowsAboutAsync("conversationId", team.Id)).Where(r => r.Kind == "ConversationKeyChanged").ToList();
        var row = Assert.Single(rows);
        Assert.NotNull(row.DispatchedAt);
        Assert.Equal(0, row.Attempts);
    }

    // ##### What a member sees #####

    [Fact]
    public async Task The_list_and_the_detail_of_a_conversation_say_which_epoch_is_newest_and_whether_it_must_be_replaced()
    {
        var team = await GroupAsync();
        var legacy = await _factory.AddLegacyGroupAsync(team.Owner.Id, "Old", team.Member.Id);
        using var owner = As(team.Owner);
        await owner.RemoveMemberResponseAsync(team.Id, team.Other.Id);

        var list = await owner.ListConversationsAsync();
        var detail = await (await owner.GetAsync($"/api/conversations/{team.Id}")).ReadJsonAsync();

        var group = list.Single(c => c.IdOf() == team.Id);
        Assert.Equal(1, group.GetProperty("keyEpoch").GetInt32());
        Assert.True(group.GetProperty("keyRotationRequired").GetBoolean());
        Assert.Equal(1, detail.GetProperty("keyEpoch").GetInt32());
        Assert.True(detail.GetProperty("keyRotationRequired").GetBoolean());
        var old = list.Single(c => c.IdOf() == legacy);
        Assert.Equal(0, old.GetProperty("keyEpoch").GetInt32());
        Assert.False(old.GetProperty("keyRotationRequired").GetBoolean());
    }
}
