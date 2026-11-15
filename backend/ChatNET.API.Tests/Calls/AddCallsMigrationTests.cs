using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Npgsql;

namespace ChatNET.API.Tests.Calls;

// The migration that adds the call log: the messages that exist stay what they were (text), the database holds a call's row apart from
// a text row, and a call is in the timeline once.
[Collection(IntegrationTestCollection.Name)]
public class AddCallsMigrationTests
{
    private const string Migration = "AddCalls";

    private readonly ChatApiFactory _factory;

    public AddCallsMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Arranged(Guid Conversation, Guid Alice, Guid Bob, Guid Message);

    private static AppUser NewUser(string name) => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"{name}_{Guid.NewGuid():N}"[..20],
        NormalizedUserName = $"{name}_{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
        Email = $"{Guid.NewGuid():N}@example.test",
        NormalizedEmail = $"{Guid.NewGuid():N}@EXAMPLE.TEST".ToUpperInvariant(),
        DisplayName = name,
        SecurityStamp = Guid.NewGuid().ToString(),
    };

    // Arranged in the schema as it was before the migration: two people and a conversation with one message.
    private static async Task<Arranged> ArrangeAsync(ScratchDatabase scratch)
    {
        var alice = NewUser("alice");
        var bob = NewUser("bob");
        await scratch.InsertAsync(alice, bob);

        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), Type = ConversationType.Direct, DirectKey = DirectKey.For(alice.Id, bob.Id),
            CreatedAt = now, UpdatedAt = now, CreatedById = alice.Id,
        };
        await scratch.AddAsync(conversation);

        var message = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Ciphertext\", \"KeyEpoch\", \"ClientMessageId\", \"CreatedAt\", \"EditCount\", \"IsDeleted\", \"ChangeSeq\", \"HasFiles\") " +
            $"VALUES ('{message}', '{conversation.Id}', '{alice.Id}', decode(repeat('01', 98), 'hex'), 1, '{Guid.NewGuid()}', '{now:O}', 0, false, 1, false)");

        return new Arranged(conversation.Id, alice.Id, bob.Id, message);
    }

    private async Task<(ScratchDatabase Scratch, Arranged Before)> MigratedAsync()
    {
        var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed(Migration);
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);
        await scratch.MigrateToAsync(migration);
        return (scratch, before);
    }

    private static Task InsertCallAsync(
        ScratchDatabase scratch, Arranged a, Guid call, string reason = "Missed", string? answeredAt = null, Guid? callee = null) =>
        scratch.ExecuteAsync(
            "INSERT INTO \"Calls\" (\"Id\", \"ConversationId\", \"CallerId\", \"CalleeId\", \"Media\", \"StartedAt\", \"AnsweredAt\", \"EndedAt\", \"EndReason\") " +
            $"VALUES ('{call}', '{a.Conversation}', '{a.Alice}', '{callee ?? a.Bob}', 'Audio', now() - interval '20 seconds', " +
            $"{(answeredAt is null ? "NULL" : $"now() - interval '{answeredAt} seconds'")}, now(), '{reason}')");

    private static Task InsertMessageAsync(
        ScratchDatabase scratch, Arranged a, string kind, Guid? call, string ciphertext = "NULL", int keyEpoch = 0, bool deleted = false, int seq = 2) =>
        scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Kind\", \"CallId\", \"Ciphertext\", \"KeyEpoch\", \"ClientMessageId\", \"CreatedAt\", \"EditCount\", \"IsDeleted\", \"ChangeSeq\", \"HasFiles\") " +
            $"VALUES ('{Guid.NewGuid()}', '{a.Conversation}', '{a.Alice}', '{kind}', {(call is null ? "NULL" : $"'{call}'")}, {ciphertext}, {keyEpoch}, '{Guid.NewGuid()}', now(), 0, {deleted.ToString().ToLowerInvariant()}, {seq}, false)");

    private const string Blob = "decode(repeat('01', 98), 'hex')";

    private static async Task AssertRefusedAsync(Task statement, string constraint)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => statement);
        var postgres = Assert.IsType<PostgresException>(error.InnerException ?? error);
        Assert.Equal(constraint, postgres.ConstraintName);
    }

    [Fact]
    public async Task The_messages_that_exist_stay_text_without_a_call()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.Equal("Text", await scratch.ScalarAsync($"SELECT \"Kind\" FROM \"Messages\" WHERE \"Id\" = '{before.Message}'"));
        Assert.Equal(1L, await scratch.ScalarAsync($"SELECT count(*) FROM \"Messages\" WHERE \"Id\" = '{before.Message}' AND \"CallId\" IS NULL"));
    }

    [Fact]
    public async Task A_call_row_has_no_text_and_names_no_key_period()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;
        var call = Guid.NewGuid();
        await InsertCallAsync(scratch, before, call);

        await InsertMessageAsync(scratch, before, "Call", call);

        Assert.Equal(1L, await scratch.ScalarAsync($"SELECT count(*) FROM \"Messages\" WHERE \"CallId\" = '{call}'"));
    }

    [Fact]
    public async Task A_call_row_that_carries_text_or_is_deleted_or_has_no_call_is_refused()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;
        var call = Guid.NewGuid();
        await InsertCallAsync(scratch, before, call);

        await AssertRefusedAsync(InsertMessageAsync(scratch, before, "Call", call, ciphertext: Blob), "CK_Messages_Content");
        await AssertRefusedAsync(InsertMessageAsync(scratch, before, "Call", call, deleted: true), "CK_Messages_Content");
        await AssertRefusedAsync(InsertMessageAsync(scratch, before, "Call", call: null), "CK_Messages_Content");
    }

    [Fact]
    public async Task A_text_row_cannot_name_a_call_or_lack_a_key_period()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;
        var call = Guid.NewGuid();
        await InsertCallAsync(scratch, before, call);

        await AssertRefusedAsync(InsertMessageAsync(scratch, before, "Text", call, ciphertext: Blob, keyEpoch: 1), "CK_Messages_Content");
        await AssertRefusedAsync(InsertMessageAsync(scratch, before, "Text", call: null, ciphertext: Blob, keyEpoch: 0), "CK_Messages_KeyEpoch");
        await AssertRefusedAsync(InsertMessageAsync(scratch, before, "Text", call: null, ciphertext: "NULL", keyEpoch: 1), "CK_Messages_Content");
    }

    [Fact]
    public async Task A_call_is_in_the_timeline_once()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;
        var call = Guid.NewGuid();
        await InsertCallAsync(scratch, before, call);
        await InsertMessageAsync(scratch, before, "Call", call, seq: 2);

        await AssertRefusedAsync(InsertMessageAsync(scratch, before, "Call", call, seq: 3), MessageConfiguration.CallIndexName);
    }

    [Fact]
    public async Task A_call_that_ended_in_an_impossible_way_is_refused()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        // Completed means someone answered; a call that rang out was not answered; nobody calls themselves; it cannot be answered
        // before it began.
        await AssertRefusedAsync(InsertCallAsync(scratch, before, Guid.NewGuid(), "Completed"), "CK_Calls_Answer");
        await AssertRefusedAsync(InsertCallAsync(scratch, before, Guid.NewGuid(), "Missed", answeredAt: "5"), "CK_Calls_Answer");
        await AssertRefusedAsync(InsertCallAsync(scratch, before, Guid.NewGuid(), callee: before.Alice), "CK_Calls_People");
        await AssertRefusedAsync(InsertCallAsync(scratch, before, Guid.NewGuid(), "Completed", answeredAt: "30"), "CK_Calls_Times");
        await InsertCallAsync(scratch, before, Guid.NewGuid(), "Completed", answeredAt: "10");
        await InsertCallAsync(scratch, before, Guid.NewGuid(), "Failed");
    }

    [Fact]
    public async Task The_migration_can_be_undone_and_takes_the_call_rows_with_it()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;
        var call = Guid.NewGuid();
        await InsertCallAsync(scratch, before, call);
        await InsertMessageAsync(scratch, before, "Call", call);

        await scratch.MigrateToAsync(scratch.MigrationBefore(scratch.MigrationNamed(Migration)));

        Assert.False(await scratch.TableExistsAsync("Calls"));
        Assert.False(await scratch.ColumnExistsAsync("Messages", "Kind"));
        Assert.False(await scratch.ColumnExistsAsync("Messages", "CallId"));
        Assert.Equal(1L, await scratch.ScalarAsync($"SELECT count(*) FROM \"Messages\" WHERE \"ConversationId\" = '{before.Conversation}'"));
    }
}
