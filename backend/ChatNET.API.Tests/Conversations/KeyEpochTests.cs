using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Conversations;

// The key periods of a conversation: how a member stores the next one, what the server insists on before it does, and what each
// member is given back.
[Collection(IntegrationTestCollection.Name)]
public class KeyEpochTests
{
    private readonly ChatApiFactory _factory;

    public KeyEpochTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Team(TestUser Owner, TestUser Member, TestUser Other, TestUser Outsider, Guid Id);

    private HttpClient As(TestUser user) => _factory.CreateClientFor(user);

    private async Task<TestUser> PersonWithKeysAsync(string prefix)
    {
        var user = await _factory.RegisterUserAsync(prefix);
        using var client = As(user);
        Assert.Equal(HttpStatusCode.Created, (await client.SetUpKeysAsync()).StatusCode);
        return user;
    }

    // A group with an owner and two members, none of whom has a key epoch yet, and a person who is not in it.
    private async Task<Team> TeamAsync()
    {
        var owner = await PersonWithKeysAsync("owner");
        var member = await PersonWithKeysAsync("member");
        var other = await PersonWithKeysAsync("other");
        var outsider = await PersonWithKeysAsync("outsider");
        var id = await _factory.AddLegacyGroupAsync(owner.Id, "Team", member.Id, other.Id);
        return new Team(owner, member, other, outsider, id);
    }

    private Task<Conversation> ConversationAsync(Guid id) =>
        _factory.QueryDbAsync(db => db.Conversations.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<List<ConversationKeyEpoch>> EpochsAsync(Guid id) =>
        _factory.QueryDbAsync(db => db.ConversationKeyEpochs.AsNoTracking().Include(e => e.Envelopes).Where(e => e.ConversationId == id)
            .OrderBy(e => e.Epoch).ToListAsync());

    private static Guid[] Everyone(Team team) => [team.Owner.Id, team.Member.Id, team.Other.Id];

    // ##### Storing an epoch #####

    [Fact]
    public async Task The_first_epoch_of_a_conversation_without_keys_is_stored_with_every_envelope_and_moves_it_on()
    {
        var team = await TeamAsync();
        var body = EpochBody.For(1, Everyone(team));
        using var owner = As(team.Owner);

        var response = await owner.PostEpochAsync(team.Id, body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var conversation = await ConversationAsync(team.Id);
        Assert.Equal(1, conversation.KeyEpoch);
        Assert.False(conversation.KeyRotationRequired);
        var epoch = Assert.Single(await EpochsAsync(team.Id));
        Assert.Equal(1, epoch.Epoch);
        Assert.Equal(team.Owner.Id, epoch.CreatedById);
        Assert.Equal(KeyEpochReason.Created, epoch.Reason);
        Assert.Equal(body.CommitmentBytes, epoch.KeyCommitment);
        Assert.Equal(body.SignatureBytes, epoch.Signature);
        Assert.Equal(3, epoch.Envelopes.Count);
        foreach (var id in Everyone(team))
        {
            var stored = Assert.Single(epoch.Envelopes, e => e.UserId == id);
            Assert.Equal(1, stored.AccountKeyGeneration);
            Assert.Equal(body.EnvelopeOf(id), stored.Envelope);
        }
    }

    [Fact]
    public async Task A_direct_conversation_gets_its_epoch_the_same_way()
    {
        var ana = await PersonWithKeysAsync("ana");
        var ben = await PersonWithKeysAsync("ben");
        var id = await _factory.AddLegacyDirectAsync(ana.Id, ben.Id);
        using var client = As(ben);

        var response = await client.PostEpochAsync(id, EpochBody.For(1, ana.Id, ben.Id));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await ConversationAsync(id)).KeyEpoch);
        Assert.Equal(2, (await EpochsAsync(id)).Single().Envelopes.Count);
    }

    [Fact]
    public async Task Any_member_may_store_the_next_epoch_not_only_a_manager()
    {
        var team = await TeamAsync();
        using var member = As(team.Member);

        var response = await member.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(team.Member.Id, (await EpochsAsync(team.Id)).Single().CreatedById);
    }

    [Fact]
    public async Task Epochs_after_the_first_are_rotations_and_each_replaces_the_one_before()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        using var member = As(team.Member);

        await owner.StoreEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));
        await member.StoreEpochAsync(team.Id, EpochBody.For(2, Everyone(team)));
        await owner.StoreEpochAsync(team.Id, EpochBody.For(3, Everyone(team)));

        Assert.Equal(3, (await ConversationAsync(team.Id)).KeyEpoch);
        var epochs = await EpochsAsync(team.Id);
        Assert.Equal([1, 2, 3], epochs.Select(e => e.Epoch));
        Assert.Equal([KeyEpochReason.Created, KeyEpochReason.Rotated, KeyEpochReason.Rotated], epochs.Select(e => e.Reason));
        Assert.Equal([team.Owner.Id, team.Member.Id, team.Owner.Id], epochs.Select(e => e.CreatedById));
    }

    [Fact]
    public async Task The_first_epoch_may_be_offered_again_only_as_the_next_one()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        var first = EpochBody.For(1, Everyone(team));
        await owner.StoreEpochAsync(team.Id, first);

        var again = await owner.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.ReadProblemAsync();
        Assert.Equal(KeyEpochCodes.Stale, problem.Code);
        var epoch = Assert.Single(await EpochsAsync(team.Id));
        Assert.Equal(first.CommitmentBytes, epoch.KeyCommitment);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task An_epoch_that_is_not_the_next_number_is_stale_and_stores_nothing(int offered)
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        await owner.StoreEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        var response = await owner.PostEpochAsync(team.Id, EpochBody.For(offered, Everyone(team)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.Stale, (await response.ReadProblemAsync()).Code);
        Assert.Equal(1, (await ConversationAsync(team.Id)).KeyEpoch);
        Assert.Single(await EpochsAsync(team.Id));
    }

    // ##### Completeness #####

    [Fact]
    public async Task An_epoch_that_leaves_a_member_out_is_refused_and_stores_nothing()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var response = await owner.PostEpochAsync(team.Id, EpochBody.For(1, team.Owner.Id, team.Member.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.MembersChanged, (await response.ReadProblemAsync()).Code);
        Assert.Equal(0, (await ConversationAsync(team.Id)).KeyEpoch);
        Assert.Empty(await EpochsAsync(team.Id));
    }

    [Fact]
    public async Task An_epoch_that_includes_someone_who_is_not_in_the_conversation_is_refused()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var response = await owner.PostEpochAsync(team.Id, EpochBody.For(1, [.. Everyone(team), team.Outsider.Id]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.MembersChanged, (await response.ReadProblemAsync()).Code);
        Assert.Empty(await EpochsAsync(team.Id));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.ConversationKeyEnvelopes.CountAsync(v => v.UserId == team.Outsider.Id)));
    }

    [Fact]
    public async Task An_envelope_sealed_to_a_key_generation_the_member_no_longer_uses_is_refused()
    {
        var team = await TeamAsync();
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountKeyBundles" ("UserId", "Generation", "Bundle", "CreatedAt")
            VALUES ({team.Member.Id}, 2, {EncryptionApi.Bundle(generation: 2)}, now())
            """));
        using var owner = As(team.Owner);

        var stale = await owner.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));
        var current = await owner.PostEpochAsync(team.Id, EpochBody.For(1, [(team.Owner.Id, 1), (team.Member.Id, 2), (team.Other.Id, 1)]));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(KeyEpochCodes.MembersChanged, (await stale.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
        Assert.Equal(2, (await EpochsAsync(team.Id)).Single().Envelopes.Single(e => e.UserId == team.Member.Id).AccountKeyGeneration);
    }

    [Fact]
    public async Task A_member_who_has_not_set_up_encryption_blocks_the_epoch_and_is_named_as_the_reason()
    {
        var owner = await PersonWithKeysAsync("owner");
        var keyless = await _factory.RegisterUserAsync("keyless");
        var id = await _factory.AddLegacyGroupAsync(owner.Id, "Team", keyless.Id);
        using var client = As(owner);

        var response = await client.PostEpochAsync(id, EpochBody.For(1, owner.Id, keyless.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(KeyEpochCodes.MemberHasNoKeys, (await response.ReadProblemAsync()).Code);
        Assert.Empty(await EpochsAsync(id));
    }

    [Fact]
    public async Task Someone_who_left_is_not_one_of_the_people_an_epoch_is_made_for()
    {
        var team = await TeamAsync();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == team.Id && m.UserId == team.Other.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        using var owner = As(team.Owner);

        var withThem = await owner.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));
        var without = await owner.PostEpochAsync(team.Id, EpochBody.For(1, team.Owner.Id, team.Member.Id));

        Assert.Equal(HttpStatusCode.Conflict, withThem.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, without.StatusCode);
        Assert.DoesNotContain((await EpochsAsync(team.Id)).Single().Envelopes, e => e.UserId == team.Other.Id);
    }

    // ##### Together, or not at all #####

    [Fact]
    public async Task Eight_members_offering_the_same_epoch_together_store_it_once()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        using var member = As(team.Member);
        using var other = As(team.Other);
        var clients = new[] { owner, member, other };

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => clients[i % 3].PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            Assert.Equal(KeyEpochCodes.Stale, (await refused.ReadProblemAsync()).Code);
        var epoch = Assert.Single(await EpochsAsync(team.Id));
        Assert.Equal(3, epoch.Envelopes.Count);
    }

    [Fact]
    public async Task Storing_an_epoch_waits_for_a_change_of_membership_that_is_in_progress()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var change = await repository.BeginMembershipChangeAsync(team.Id, default);
        Assert.NotNull(change);

        var posted = owner.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));
        var finishedWhileHeld = await Task.WhenAny(posted, Task.Delay(500)) == posted;
        await change.DisposeAsync();
        var response = await posted;

        Assert.False(finishedWhileHeld);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_failure_storing_the_epoch_leaves_the_conversation_as_it_was()
    {
        var team = await TeamAsync();
        // A row that already exists for the epoch makes the insert fail, which must leave nothing of it and the conversation unmoved.
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ConversationKeyEpochs" ("ConversationId", "Epoch", "CreatedById", "Reason", "CreatedAt", "KeyCommitment", "Signature")
            VALUES ({team.Id}, 1, {team.Owner.Id}, 'Created', now(), {new byte[32]}, {new byte[64]})
            """));
        using var owner = As(team.Owner);

        var response = await owner.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, (await ConversationAsync(team.Id)).KeyEpoch);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.ConversationKeyEnvelopes.CountAsync(v => v.ConversationId == team.Id)));
    }

    // ##### Who may store #####

    [Fact]
    public async Task Someone_who_is_not_in_the_conversation_may_not_store_an_epoch_for_it()
    {
        var team = await TeamAsync();
        using var outsider = As(team.Outsider);

        var response = await outsider.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await EpochsAsync(team.Id));
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_answers_as_one_the_caller_is_not_in()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var response = await owner.PostEpochAsync(Guid.NewGuid(), EpochBody.For(1, team.Owner.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Someone_who_left_may_not_store_an_epoch()
    {
        var team = await TeamAsync();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == team.Id && m.UserId == team.Other.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        using var gone = As(team.Other);

        var response = await gone.PostEpochAsync(team.Id, EpochBody.For(1, team.Owner.Id, team.Member.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await EpochsAsync(team.Id));
    }

    [Fact]
    public async Task Storing_and_reading_epochs_need_a_signed_in_person()
    {
        var team = await TeamAsync();
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostEpochAsync(team.Id, EpochBody.For(1, team.Owner.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetEpochsAsync(team.Id)).StatusCode);
    }

    // ##### Validation #####

    public static TheoryData<string, Action<EpochBody>> BadEpochs => new()
    {
        { "no epoch number", b => b.Epoch = null },
        { "epoch 0", b => b.Epoch = 0 },
        { "a negative epoch", b => b.Epoch = -1 },
        { "no commitment", b => b.Commitment = null },
        { "a commitment one byte short", b => b.Commitment = EncryptionApi.B64(new byte[31]) },
        { "a commitment one byte long", b => b.Commitment = EncryptionApi.B64(new byte[33]) },
        { "a commitment that is not base64url", b => b.Commitment = "not base64url!" },
        { "no signature", b => b.Signature = null },
        { "a signature one byte short", b => b.Signature = EncryptionApi.B64(new byte[63]) },
        { "a signature one byte long", b => b.Signature = EncryptionApi.B64(new byte[65]) },
        { "no envelopes", b => b.Envelopes = null },
        { "an empty list of envelopes", b => b.Envelopes = [] },
        { "an envelope one byte short", b => b.Envelopes![0].Envelope = EncryptionApi.B64(EncryptionApi.Envelope(length: 93)) },
        { "an envelope one byte long", b => b.Envelopes![0].Envelope = EncryptionApi.B64(EncryptionApi.Envelope(length: 95)) },
        { "an envelope of an unknown version", b => b.Envelopes![0].Envelope = EncryptionApi.B64(EncryptionApi.Envelope(version: 2)) },
        { "an envelope of an unknown suite", b => b.Envelopes![0].Envelope = EncryptionApi.B64(EncryptionApi.Envelope(suite: 2)) },
        { "an envelope that is not base64url", b => b.Envelopes![0].Envelope = "not base64url!" },
        { "an envelope with no value", b => b.Envelopes![0].Envelope = null },
        { "an envelope with no person", b => b.Envelopes![0].UserId = null },
        { "an envelope for an empty id", b => b.Envelopes![0].UserId = Guid.Empty },
        { "an envelope with no generation", b => b.Envelopes![0].Generation = null },
        { "generation 0", b => b.Envelopes![0].Generation = 0 },
        { "two envelopes for one person", b => b.Envelopes![1].UserId = b.Envelopes[0].UserId },
        { "more envelopes than a group can have", b => b.Envelopes = Enumerable.Range(0, 102)
            .Select(_ => new EnvelopeBody { UserId = Guid.NewGuid(), Generation = 1, Envelope = EncryptionApi.B64(EncryptionApi.Envelope()) }).ToList() },
    };

    [Theory]
    [MemberData(nameof(BadEpochs))]
    public async Task An_epoch_that_is_not_well_formed_is_refused_and_stores_nothing(string _, Action<EpochBody> spoil)
    {
        var team = await TeamAsync();
        var body = EpochBody.For(1, Everyone(team));
        spoil(body);
        using var owner = As(team.Owner);

        var response = await owner.PostEpochAsync(team.Id, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull((await response.ReadProblemAsync()).Errors);
        Assert.Equal(0, (await ConversationAsync(team.Id)).KeyEpoch);
        Assert.Empty(await EpochsAsync(team.Id));
    }

    [Fact]
    public async Task A_refused_epoch_never_repeats_what_was_sent()
    {
        var team = await TeamAsync();
        var body = EpochBody.For(1, Everyone(team));
        body.Commitment = EncryptionApi.B64(new byte[31]);
        body.Envelopes![0].Envelope = EncryptionApi.B64(EncryptionApi.Envelope(version: 9));
        using var owner = As(team.Owner);

        var text = await (await owner.PostEpochAsync(team.Id, body)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(body.Commitment, text);
        Assert.DoesNotContain(body.Envelopes[0].Envelope!, text);
    }

    [Fact]
    public async Task A_request_with_no_body_is_refused_without_an_error()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var response = await owner.PostAsync($"/api/conversations/{team.Id}/key-epochs", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ##### Reading #####

    [Fact]
    public async Task A_member_is_given_their_own_envelope_and_the_proof_but_never_anyone_elses()
    {
        var team = await TeamAsync();
        var body = EpochBody.For(1, Everyone(team));
        using var owner = As(team.Owner);
        using var member = As(team.Member);
        await owner.StoreEpochAsync(team.Id, body);

        var page = await member.ReadEpochsAsync(team.Id);

        Assert.False(page.GetProperty("hasMore").GetBoolean());
        var epoch = Assert.Single(page.GetProperty("epochs").EnumerateArray());
        Assert.Equal(1, epoch.GetProperty("epoch").GetInt32());
        Assert.Equal(team.Owner.Id, epoch.GetProperty("creatorId").GetGuid());
        Assert.Equal("Created", epoch.GetProperty("reason").GetString());
        Assert.Equal(body.CommitmentBytes, EncryptionApi.FromB64(epoch.GetProperty("commitment").GetString()!));
        Assert.Equal(body.SignatureBytes, EncryptionApi.FromB64(epoch.GetProperty("signature").GetString()!));
        Assert.Equal(1, epoch.GetProperty("generation").GetInt32());
        Assert.Equal(body.EnvelopeOf(team.Member.Id), EncryptionApi.FromB64(epoch.GetProperty("envelope").GetString()!));
        Assert.Equal(
            Everyone(team).Order().ToList(),
            epoch.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()).ToList());
        Assert.All(epoch.GetProperty("members").EnumerateArray(), m => Assert.Equal(1, m.GetProperty("generation").GetInt32()));
        Assert.Equal(DateTimeKind.Utc, epoch.GetProperty("createdAt").GetDateTime().Kind);

        var text = page.GetRawText();
        foreach (var other in new[] { team.Owner.Id, team.Other.Id })
            Assert.DoesNotContain(body.Envelopes!.Single(e => e.UserId == other).Envelope!, text);
    }

    [Fact]
    public async Task A_conversation_with_no_epoch_returns_an_empty_page()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var page = await owner.ReadEpochsAsync(team.Id);

        Assert.Equal(0, page.GetProperty("epochs").GetArrayLength());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Epochs_come_oldest_first_in_pages_and_a_page_says_whether_there_are_more()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        for (var epoch = 1; epoch <= 5; epoch++)
            await owner.StoreEpochAsync(team.Id, EpochBody.For(epoch, Everyone(team)));

        static List<int> Numbers(JsonElement page) =>
            page.GetProperty("epochs").EnumerateArray().Select(e => e.GetProperty("epoch").GetInt32()).ToList();

        var first = await owner.ReadEpochsAsync(team.Id, limit: 2);
        var second = await owner.ReadEpochsAsync(team.Id, from: 3, limit: 2);
        var last = await owner.ReadEpochsAsync(team.Id, from: 5, limit: 2);
        var all = await owner.ReadEpochsAsync(team.Id);
        var beyond = await owner.ReadEpochsAsync(team.Id, from: 6);

        Assert.Equal([1, 2], Numbers(first));
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal([3, 4], Numbers(second));
        Assert.True(second.GetProperty("hasMore").GetBoolean());
        Assert.Equal([5], Numbers(last));
        Assert.False(last.GetProperty("hasMore").GetBoolean());
        Assert.Equal([1, 2, 3, 4, 5], Numbers(all));
        Assert.False(all.GetProperty("hasMore").GetBoolean());
        Assert.Empty(Numbers(beyond));
        Assert.False(beyond.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Exactly_a_page_of_epochs_does_not_claim_there_are_more()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        for (var epoch = 1; epoch <= 2; epoch++)
            await owner.StoreEpochAsync(team.Id, EpochBody.For(epoch, Everyone(team)));

        var page = await owner.ReadEpochsAsync(team.Id, limit: 2);

        Assert.Equal(2, page.GetProperty("epochs").GetArrayLength());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("from=0")]
    [InlineData("from=-1")]
    [InlineData("limit=0")]
    [InlineData("limit=51")]
    [InlineData("limit=-3")]
    public async Task A_page_that_cannot_be_returned_is_refused(string query)
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        var response = await owner.GetAsync($"/api/conversations/{team.Id}/key-epochs?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_largest_page_is_accepted()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetEpochsAsync(team.Id, limit: 50)).StatusCode);
    }

    [Fact]
    public async Task A_person_added_later_is_given_only_the_epochs_made_for_them()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        await owner.StoreEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));
        await _factory.ExecuteDbAsync(async db =>
        {
            db.ConversationMembers.Add(new ConversationMember
            {
                ConversationId = team.Id, UserId = team.Outsider.Id, Role = MemberRole.Member, JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        await owner.StoreEpochAsync(team.Id, EpochBody.For(2, [.. Everyone(team), team.Outsider.Id]));
        using var newcomer = As(team.Outsider);

        var page = await newcomer.ReadEpochsAsync(team.Id);

        var epoch = Assert.Single(page.GetProperty("epochs").EnumerateArray());
        Assert.Equal(2, epoch.GetProperty("epoch").GetInt32());
        Assert.Equal(4, epoch.GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task Someone_who_left_keeps_the_epochs_they_were_in_and_is_not_given_the_ones_after()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        await owner.StoreEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));
        await owner.StoreEpochAsync(team.Id, EpochBody.For(2, Everyone(team)));
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == team.Id && m.UserId == team.Other.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        await owner.StoreEpochAsync(team.Id, EpochBody.For(3, team.Owner.Id, team.Member.Id));
        using var gone = As(team.Other);
        using var staying = As(team.Member);

        var leaver = await gone.ReadEpochsAsync(team.Id);
        var member = await staying.ReadEpochsAsync(team.Id);

        Assert.Equal([1, 2], leaver.GetProperty("epochs").EnumerateArray().Select(e => e.GetProperty("epoch").GetInt32()));
        Assert.Equal([1, 2, 3], member.GetProperty("epochs").EnumerateArray().Select(e => e.GetProperty("epoch").GetInt32()));
    }

    [Fact]
    public async Task Someone_who_was_never_in_the_conversation_is_given_nothing()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);
        await owner.StoreEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));
        using var outsider = As(team.Outsider);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetEpochsAsync(team.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetEpochsAsync(Guid.NewGuid())).StatusCode);
    }

    // ##### Telling the members #####

    [Fact]
    public async Task Storing_an_epoch_tells_the_people_in_the_conversation_and_nobody_else()
    {
        var team = await TeamAsync();
        await using var ownerTab = await HubClient.ConnectAsync(_factory, team.Owner);
        await using var memberTab = await HubClient.ConnectAsync(_factory, team.Member);
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, team.Outsider);
        using var owner = As(team.Owner);

        await owner.StoreEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        foreach (var tab in new[] { ownerTab, memberTab })
        {
            var state = await tab.WaitForKeyChangedAsync(team.Id);
            Assert.Equal(1, state.GetProperty("keyEpoch").GetInt32());
            Assert.False(state.GetProperty("keyRotationRequired").GetBoolean());
        }

        Assert.False(await outsiderTab.ReceivedKeyChangedAsync(team.Id));
    }

    [Fact]
    public async Task The_push_is_made_by_the_request_and_marked_done_at_once()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        await owner.StoreEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("conversationId", team.Id), r => r.Kind == "ConversationKeyChanged");
        Assert.NotNull(row.DispatchedAt);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task A_refused_epoch_leaves_no_push_behind()
    {
        var team = await TeamAsync();
        using var owner = As(team.Owner);

        await owner.PostEpochAsync(team.Id, EpochBody.For(1, team.Owner.Id));

        Assert.DoesNotContain(await _factory.OutboxRowsAboutAsync("conversationId", team.Id), r => r.Kind == "ConversationKeyChanged");
    }

    // ##### Access #####

    [Fact]
    public async Task Epoch_endpoints_share_the_allowance_of_the_other_key_endpoints()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Keys:PermitLimit"] = "2",
                ["RateLimiting:Keys:WindowSeconds"] = "60",
            })));
        var team = await TeamAsync();
        using var person = host.ClientFor(await _factory.RegisterUserAsync("limited"));

        await person.GetEpochsAsync(team.Id);
        await person.GetAsync("/api/keys/me");
        var refused = await person.PostEpochAsync(team.Id, EpochBody.For(1, Everyone(team)));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains(refused.Headers, h => h.Key == "Retry-After");
    }
}
