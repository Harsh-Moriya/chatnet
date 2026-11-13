using System.Net;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Messaging;

// The point of the numbers: a client that follows the feed by its cursor, while other people send, edit and delete at the same time, must
// end up holding every message in its final state. A feed numbered by a Postgres sequence fails this: a transaction draws its number
// before it commits, so a reader can see number 12, move its cursor past 11, and never see 11 when it commits a moment later. Here the
// number is taken under the conversation's row lock, so that cannot happen, and this test is what notices if it ever does.
[Collection(IntegrationTestCollection.Name)]
public class ChangesFeedConcurrencyTests
{
    private const int Writers = 3;
    private const int RoundsPerWriter = 14;

    private readonly ChatApiFactory _factory;

    public ChangesFeedConcurrencyTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Each writer sends a message every round, edits one of its earlier messages every third round and deletes one every fourth.
    private static async Task WriteAsync(HttpClient client, Guid conversation, string name)
    {
        var mine = new List<(Guid Id, int Edits, bool Deleted)>();

        for (var round = 0; round < RoundsPerWriter; round++)
        {
            var sent = await client.SendMessageAsync(conversation, $"{name} {round}");
            mine.Add((sent.MessageIdOf(), 0, false));

            if (round % 3 == 2)
            {
                var at = mine.FindIndex(m => !m.Deleted);
                var (id, edits, _) = mine[at];
                var response = await client.PatchMessageAsync(conversation, id, $"{name} {round} edited", editCount: edits + 1);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                mine[at] = (id, edits + 1, false);
            }

            if (round % 4 == 3)
            {
                var at = mine.FindLastIndex(m => !m.Deleted);
                var response = await client.DeleteMessageResponseAsync(conversation, mine[at].Id);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                mine[at] = (mine[at].Id, mine[at].Edits, true);
            }
        }
    }

    [Fact]
    public async Task A_reader_that_follows_the_feed_while_others_send_edit_and_delete_ends_with_every_message_in_its_final_state()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var second = await _factory.RegisterUserAsync("second");
        var third = await _factory.RegisterUserAsync("third");
        using var ownerClient = _factory.CreateClientFor(owner);
        var conversation = (await ownerClient.CreateGroupAsync("Busy", second.Id, third.Id)).IdOf();
        using var secondClient = _factory.CreateClientFor(second);
        using var thirdClient = _factory.CreateClientFor(third);
        using var reader = _factory.CreateClientFor(owner);

        var known = new Dictionary<Guid, JsonElement>();
        var cursor = 0L;
        var writing = true;

        var following = Task.Run(async () =>
        {
            while (true)
            {
                var stop = !Volatile.Read(ref writing);
                var page = await reader.GetChangesAsync(conversation, cursor);

                var numbers = page.Changes.Select(m => m.ChangeSeqOf()).ToList();
                Assert.All(numbers, n => Assert.InRange(n, cursor + 1, page.Cursor));
                Assert.Equal(numbers.OrderBy(n => n), numbers);

                foreach (var message in page.Changes)
                {
                    var id = message.MessageIdOf();
                    if (!known.TryGetValue(id, out var held) || message.ChangeSeqOf() > held.ChangeSeqOf())
                        known[id] = message.Clone();
                }

                cursor = page.Cursor;
                if (stop && !page.More)
                    return;

                await Task.Delay(2);
            }
        });

        await Task.WhenAll(WriteAsync(ownerClient, conversation, "owner"), WriteAsync(secondClient, conversation, "second"), WriteAsync(thirdClient, conversation, "third"));
        Volatile.Write(ref writing, false);
        await following;

        var stored = await _factory.QueryDbAsync(db => db.Messages
            .Where(m => m.ConversationId == conversation)
            .Select(m => new { m.Id, m.EditCount, m.IsDeleted, m.ChangeSeq })
            .ToListAsync());
        var counter = await _factory.QueryDbAsync(db => db.Conversations.Where(c => c.Id == conversation).Select(c => c.ChangeSeq).SingleAsync());

        Assert.Equal(Writers * RoundsPerWriter, stored.Count);
        Assert.Equal(stored.Count, known.Count);
        foreach (var row in stored)
        {
            var seen = known[row.Id];
            Assert.Equal(row.ChangeSeq, seen.ChangeSeqOf());
            Assert.Equal(row.EditCount, seen.GetProperty("editCount").GetInt32());
            Assert.Equal(row.IsDeleted, seen.GetProperty("isDeleted").GetBoolean());
        }

        Assert.Equal(counter, cursor);
        Assert.Equal(counter, stored.Max(m => m.ChangeSeq));
        Assert.Equal(stored.Count, stored.Select(m => m.ChangeSeq).Distinct().Count());
    }

    // Every change took one number: each message was sent once, and each edit and each delete took one more.
    [Fact]
    public async Task The_counter_after_many_changes_made_together_is_the_number_of_changes_that_were_stored()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var other = await _factory.RegisterUserAsync("other");
        using var ownerClient = _factory.CreateClientFor(owner);
        var conversation = (await ownerClient.CreateGroupAsync("Busy", other.Id)).IdOf();
        using var otherClient = _factory.CreateClientFor(other);

        await Task.WhenAll(WriteAsync(ownerClient, conversation, "owner"), WriteAsync(otherClient, conversation, "other"));

        var sends = 2 * RoundsPerWriter;
        var edits = 2 * Enumerable.Range(0, RoundsPerWriter).Count(round => round % 3 == 2);
        var deletes = 2 * Enumerable.Range(0, RoundsPerWriter).Count(round => round % 4 == 3);
        var counter = await _factory.QueryDbAsync(db => db.Conversations.Where(c => c.Id == conversation).Select(c => c.ChangeSeq).SingleAsync());

        Assert.Equal(sends + edits + deletes, counter);
    }
}
