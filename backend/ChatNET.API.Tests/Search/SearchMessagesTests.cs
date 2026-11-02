using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Search;

// Search finds what a person may read, and only that: messages of the conversations they are in, sent while they were in
// them, not deleted. Words are matched from their start ("repo" finds "report"), every word typed must be there, and the
// newest come first, twenty at a time.
[Collection(IntegrationTestCollection.Name)]
public class SearchMessagesTests
{
    private readonly ChatApiFactory _factory;

    public SearchMessagesTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // A word nothing else in the shared test database contains, so a search finds only what the test arranged.
    private static string Unique() => $"zq{Guid.NewGuid():N}"[..14];

    private static DateTime Ago(int minutes) => DateTime.UtcNow.AddMinutes(-minutes);

    // ##### Finding #####

    [Fact]
    public async Task A_word_finds_the_message_with_the_ids_and_names_needed_to_open_it()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var word = Unique();
        var sent = await client.SendMessageAsync(conversation, $"the {word} is ready");

        var hits = await client.SearchAsync(word);

        var hit = Assert.Single(hits);
        Assert.Equal(sent.MessageIdOf(), hit.HitIdOf());
        Assert.Equal(conversation, hit.GetProperty("conversationId").GetGuid());
        Assert.Equal(alice.Id, hit.GetProperty("senderId").GetGuid());
        Assert.Equal(alice.DisplayName, hit.GetProperty("senderDisplayName").GetString());
        Assert.Equal(bob.DisplayName, hit.GetProperty("conversationName").GetString());
        Assert.Equal("Direct", hit.GetProperty("conversationType").GetString());
        Assert.Equal(word, hit.MatchedOf());
        Assert.Contains("is ready", hit.SnippetOf());
        Assert.Null(hit.FileNameOf());
    }

    [Fact]
    public async Task The_match_does_not_depend_on_case()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var word = Unique();
        await client.SendMessageAsync(conversation, $"Quarterly {word.ToUpperInvariant()}");

        Assert.Single(await client.SearchAsync(word));
        Assert.Single(await client.SearchAsync(word.ToUpperInvariant()));
    }

    [Fact]
    public async Task The_last_word_matches_from_its_start_and_a_complete_word_does_not_match_the_middle_of_another()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var stem = Unique();
        await client.SendMessageAsync(conversation, $"{stem}report is late");

        var byStart = await client.SearchAsync($"{stem}repo");
        var byMiddle = await client.SearchAsync($"{stem}port");
        var byEarlierWordIncomplete = await client.SearchAsync($"{stem}repo late");

        Assert.Single(byStart);
        Assert.Equal($"{stem}report", byStart[0].MatchedOf());
        Assert.Empty(byMiddle);
        Assert.Empty(byEarlierWordIncomplete);
    }

    [Fact]
    public async Task Every_word_typed_has_to_be_in_the_message_in_any_order()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var one = Unique();
        var two = Unique();
        var both = await client.SendMessageAsync(conversation, $"{two} then {one}");
        await client.SendMessageAsync(conversation, $"only {one} here");

        var hits = await client.SearchAsync($"{one} {two}");

        Assert.Equal(both.MessageIdOf(), Assert.Single(hits).HitIdOf());
    }

    [Fact]
    public async Task Quotes_ask_for_a_phrase_and_a_minus_leaves_out_messages_with_a_word()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var one = Unique();
        var two = Unique();
        var adjacent = await client.SendMessageAsync(conversation, $"{one} {two} agreed");
        await client.SendMessageAsync(conversation, $"{one} and then much later {two}");
        var draft = await client.SendMessageAsync(conversation, $"{one} draft");
        var final = await client.SendMessageAsync(conversation, $"{one} final");

        var phrase = await client.SearchAsync($"\"{one} {two}\"");
        var without = await client.SearchAsync($"{one} -draft");

        Assert.Equal(adjacent.MessageIdOf(), Assert.Single(phrase).HitIdOf());
        Assert.DoesNotContain(without, h => h.HitIdOf() == draft.MessageIdOf());
        Assert.Contains(without, h => h.HitIdOf() == final.MessageIdOf());
    }

    [Fact]
    public async Task Or_finds_messages_with_either_word_even_when_the_second_is_the_last_word_typed()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var one = Unique();
        var two = Unique();
        var first = await client.SendMessageAsync(conversation, $"{one} only");
        var second = await client.SendMessageAsync(conversation, $"{two} only");
        await client.SendMessageAsync(conversation, "neither of them");

        var either = await client.SearchAsync($"{one} or {two}");
        var either_in_capitals = await client.SearchAsync($"{one} OR {two}");

        Assert.Equal([second.MessageIdOf(), first.MessageIdOf()], either.Select(h => h.HitIdOf()));
        Assert.Equal([second.MessageIdOf(), first.MessageIdOf()], either_in_capitals.Select(h => h.HitIdOf()));
    }

    [Fact]
    public async Task Nothing_found_is_an_empty_list_and_not_an_error()
    {
        var (alice, _, _) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.SearchResponseAsync(Unique());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("a & b")]
    [InlineData("foo:*")]
    [InlineData("foo <-> bar")]
    [InlineData("(((")]
    [InlineData("\\\\")]
    [InlineData("'; DROP TABLE \"Messages\"; --")]
    [InlineData("!!")]
    [InlineData("\"\"")]
    [InlineData("привет мир")]
    [InlineData("😀😀")]
    public async Task Odd_text_is_never_an_error(string typed)
    {
        var (alice, _, _) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.SearchResponseAsync(typed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Newest_come_first()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        await _factory.AddMessageAsync(conversation, alice.Id, $"{word} oldest", Ago(30));
        await _factory.AddMessageAsync(conversation, alice.Id, $"{word} newest", Ago(10));
        await _factory.AddMessageAsync(conversation, alice.Id, $"{word} middle", Ago(20));

        var hits = await client.SearchAsync(word);

        Assert.Equal(["newest", "middle", "oldest"], hits.Select(h => h.SnippetOf().Split(' ')[^1]));
    }

    [Fact]
    public async Task An_edited_message_is_found_by_its_new_text_and_not_the_old()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var before = Unique();
        var after = Unique();
        var sent = await client.SendMessageAsync(conversation, $"say {before}");

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), $"say {after}");

        Assert.Empty(await client.SearchAsync(before));
        Assert.Equal(sent.MessageIdOf(), Assert.Single(await client.SearchAsync(after)).HitIdOf());
    }

    [Fact]
    public async Task A_deleted_message_is_not_found()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var word = Unique();
        var sent = await client.SendMessageAsync(conversation, $"secret {word}");

        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        Assert.Empty(await client.SearchAsync(word));
    }

    [Fact]
    public async Task A_deleted_message_that_still_holds_its_text_in_the_database_is_not_found_either()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        await _factory.AddMessageAsync(conversation, alice.Id, $"gone {word}", Ago(5), deleted: true);

        Assert.Empty(await client.SearchAsync(word));
    }

    // The recent phase reads the newest 300 messages of a conversation; what is older is found through the full text index, a second
    // query with rules of its own (the window, deletion) that a small conversation never reaches.
    [Fact]
    public async Task What_is_older_than_the_recent_messages_is_found_through_the_index_without_the_deleted_ones()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        var kept = Unique();
        await _factory.AddMessageAsync(conversation, alice.Id, $"{word} {kept}", Ago(60 * 20));
        await _factory.AddMessageAsync(conversation, alice.Id, $"{word} removed", Ago(60 * 20 - 1), deleted: true);
        await _factory.AddMessagesAsync(conversation, alice.Id, count: 350, firstAt: Ago(600), step: TimeSpan.FromSeconds(10));

        var hits = await client.SearchAsync(word);

        Assert.Equal([$"{word} {kept}"], hits.Select(h => h.SnippetOf()));
    }

    [Fact]
    public async Task A_group_is_named_as_it_is_and_a_direct_chat_after_the_other_person_for_each_side()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var direct = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        var group = (await aliceClient.CreateGroupAsync("Launch crew", bob.Id)).IdOf();
        var word = Unique();
        await aliceClient.SendMessageAsync(direct, $"{word} direct");
        await aliceClient.SendMessageAsync(group, $"{word} group");

        var forAlice = await aliceClient.SearchAsync(word);
        var forBob = await bobClient.SearchAsync(word);

        Assert.Equal(bob.DisplayName, forAlice.Single(h => h.GetProperty("conversationId").GetGuid() == direct).GetProperty("conversationName").GetString());
        Assert.Equal(alice.DisplayName, forBob.Single(h => h.GetProperty("conversationId").GetGuid() == direct).GetProperty("conversationName").GetString());
        Assert.Equal("Launch crew", forBob.Single(h => h.GetProperty("conversationId").GetGuid() == group).GetProperty("conversationName").GetString());
        Assert.Equal("Group", forBob.Single(h => h.GetProperty("conversationId").GetGuid() == group).GetProperty("conversationType").GetString());
    }

    [Fact]
    public async Task Text_that_holds_the_match_markers_cannot_fake_a_match()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var word = Unique();
        await client.SendMessageAsync(conversation, $"before fake and {word} after");

        var hit = Assert.Single(await client.SearchAsync(word));

        Assert.Equal(word, hit.MatchedOf());
        Assert.DoesNotContain('', hit.SnippetOf());
        Assert.DoesNotContain('', hit.SnippetOf());
    }

    // ##### Only what the person may read #####

    [Fact]
    public async Task Messages_of_conversations_the_person_is_not_in_are_not_found()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var outsiderClient = _factory.CreateClientFor(outsider);
        var word = Unique();
        await aliceClient.SendMessageAsync(conversation, $"private {word}");

        Assert.Single(await aliceClient.SearchAsync(word));
        Assert.Empty(await outsiderClient.SearchAsync(word));
    }

    [Fact]
    public async Task What_was_said_before_the_person_joined_is_not_found()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var late = await _factory.RegisterUserAsync("late");
        using var ownerClient = _factory.CreateClientFor(owner);
        using var lateClient = _factory.CreateClientFor(late);
        var other = await _factory.RegisterUserAsync("other");
        var group = (await ownerClient.CreateGroupAsync("Team", other.Id)).IdOf();
        var word = Unique();
        await _factory.BackdateJoinAsync(group);
        await _factory.AddMessageAsync(group, owner.Id, $"{word} early", Ago(60));
        await ownerClient.AddMembersAsync(group, late.Id);
        await ownerClient.SendMessageAsync(group, $"{word} later");

        var hits = await lateClient.SearchAsync(word);

        Assert.Equal("later", Assert.Single(hits).SnippetOf().Split(' ')[^1]);
        Assert.Equal(2, (await ownerClient.SearchAsync(word)).Count);
    }

    [Fact]
    public async Task Someone_who_left_finds_what_was_said_while_they_were_in_and_not_what_was_said_after()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var leaver = await _factory.RegisterUserAsync("leaver");
        using var ownerClient = _factory.CreateClientFor(owner);
        using var leaverClient = _factory.CreateClientFor(leaver);
        var group = (await ownerClient.CreateGroupAsync("Team", leaver.Id)).IdOf();
        var word = Unique();
        await ownerClient.SendMessageAsync(group, $"{word} during");
        (await leaverClient.LeaveResponseAsync(group)).EnsureSuccessStatusCode();
        await ownerClient.SendMessageAsync(group, $"{word} after");

        var hits = await leaverClient.SearchAsync(word);

        Assert.Equal("during", Assert.Single(hits).SnippetOf().Split(' ')[^1]);
    }

    // ##### One conversation #####

    [Fact]
    public async Task A_conversation_named_limits_the_search_to_it()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var direct = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var group = (await client.CreateGroupAsync("Team", bob.Id)).IdOf();
        var word = Unique();
        await client.SendMessageAsync(direct, $"{word} one");
        await client.SendMessageAsync(group, $"{word} two");

        var inGroup = await client.SearchAsync(word, conversationId: group);

        Assert.Equal(group, Assert.Single(inGroup).GetProperty("conversationId").GetGuid());
        Assert.Equal(2, (await client.SearchAsync(word)).Count);
    }

    [Fact]
    public async Task Naming_a_conversation_the_person_is_not_in_is_the_answer_every_endpoint_gives()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(outsider);

        var known = await client.SearchResponseAsync("anything", conversation);
        var unknown = await client.SearchResponseAsync("anything", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, known.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Contains("You are not a member of this conversation.", await known.Content.ReadAsStringAsync());
    }

    // ##### Files #####

    private async Task<(Guid MessageId, Guid FileId)> MessageWithFileAsync(
        Guid conversation, Guid sender, string content, string fileName, DateTime at)
    {
        var (messageId, files) = await _factory.AddMessageWithFilesAsync(conversation, sender, content, at, 1);
        await _factory.ExecuteDbAsync(db => db.MessageAttachments
            .Where(a => a.Id == files[0])
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.FileName, fileName)));

        return (messageId, files[0]);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("quarterly")]
    [InlineData("quart")]
    [InlineData("v2")]
    [InlineData("report")]
    public async Task A_message_is_found_by_a_word_of_the_name_of_its_file(string word)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var tag = Unique();
        var (messageId, _) = await MessageWithFileAsync(conversation, alice.Id, "", $"Quarterly-Report_v2.{tag}.pdf", Ago(5));

        // The word goes last: only the last word is matched from its start.
        var hits = await client.SearchAsync($"{tag} {word}");

        var hit = Assert.Single(hits);
        Assert.Equal(messageId, hit.HitIdOf());
        Assert.Equal($"Quarterly-Report_v2.{tag}.pdf", hit.FileNameOf());
        Assert.Empty(hit.GetProperty("snippet").EnumerateArray());
    }

    [Fact]
    public async Task A_message_whose_text_and_file_both_match_is_listed_once_with_its_text()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        var (messageId, _) = await MessageWithFileAsync(conversation, alice.Id, $"see the {word} file", $"{word}.pdf", Ago(5));

        var hit = Assert.Single(await client.SearchAsync(word));

        Assert.Equal(messageId, hit.HitIdOf());
        Assert.Equal(word, hit.MatchedOf());
        Assert.Null(hit.FileNameOf());
    }

    [Fact]
    public async Task A_message_with_two_files_that_match_is_listed_once()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        var (messageId, files) = await _factory.AddMessageWithFilesAsync(conversation, alice.Id, "", Ago(5), 2);
        for (var i = 0; i < files.Count; i++)
        {
            var name = $"{word}-{i}.png";
            var id = files[i];
            await _factory.ExecuteDbAsync(db => db.MessageAttachments
                .Where(a => a.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.FileName, name)));
        }

        var hit = Assert.Single(await client.SearchAsync(word));

        Assert.Equal(messageId, hit.HitIdOf());
    }

    [Fact]
    public async Task The_file_of_a_deleted_message_is_not_found()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        var (messageId, _) = await MessageWithFileAsync(conversation, alice.Id, "", $"{word}.pdf", Ago(5));
        await _factory.ExecuteDbAsync(db => db.Messages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true)));

        Assert.Empty(await client.SearchAsync(word));
    }

    [Fact]
    public async Task Files_of_conversations_the_person_is_not_in_are_not_found()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var outsiderClient = _factory.CreateClientFor(outsider);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        await MessageWithFileAsync(conversation, alice.Id, "", $"{word}.pdf", Ago(5));

        Assert.Empty(await outsiderClient.SearchAsync(word));
    }

    // ##### Pages #####

    [Fact]
    public async Task Results_come_twenty_at_a_time_and_the_cursor_continues_without_skipping_or_repeating()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        await _factory.AddMessagesAsync(conversation, alice.Id, 45, Ago(120), TimeSpan.FromMinutes(1), prefix: word);

        var first = await client.SearchAsync(word);
        var second = await client.SearchAsync(word, before: first[^1].HitIdOf());
        var third = await client.SearchAsync(word, before: second[^1].HitIdOf());
        var fourth = await client.SearchAsync(word, before: third[^1].HitIdOf());

        Assert.Equal([20, 20, 5, 0], [first.Count, second.Count, third.Count, fourth.Count]);
        var all = first.Concat(second).Concat(third).Select(h => h.HitIdOf()).ToList();
        Assert.Equal(45, all.Distinct().Count());
        Assert.Equal($"{word} 44", first[0].SnippetOf());
        Assert.Equal($"{word} 0", third[^1].SnippetOf());
    }

    [Fact]
    public async Task Messages_stamped_with_the_same_instant_are_neither_skipped_nor_repeated_between_pages()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.BackdateJoinAsync(conversation);
        var word = Unique();
        var instant = Ago(30);
        for (var i = 0; i < 25; i++)
            await _factory.AddMessageAsync(conversation, alice.Id, $"{word} {i}", instant);

        var first = await client.SearchAsync(word);
        var second = await client.SearchAsync(word, before: first[^1].HitIdOf());

        Assert.Equal(20, first.Count);
        Assert.Equal(5, second.Count);
        Assert.Equal(25, first.Concat(second).Select(h => h.HitIdOf()).Distinct().Count());
    }

    [Fact]
    public async Task A_cursor_that_is_not_a_message_the_person_may_read_is_refused_with_one_answer()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var (stranger, _, strangersConversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        using var strangerClient = _factory.CreateClientFor(stranger);
        var group = (await client.CreateGroupAsync("Team", (await _factory.RegisterUserAsync("member")).Id)).IdOf();
        var word = Unique();
        var mine = await client.SendMessageAsync(conversation, word);
        var theirs = await strangerClient.SendMessageAsync(strangersConversation, word);

        var unknown = await client.SearchResponseAsync(word, before: Guid.NewGuid());
        var foreign = await client.SearchResponseAsync(word, before: theirs.MessageIdOf());
        var elsewhere = await client.SearchResponseAsync(word, conversationId: group, before: mine.MessageIdOf());
        var fine = await client.SearchResponseAsync(word, conversationId: conversation, before: mine.MessageIdOf());

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal(Detail(await unknown.Content.ReadAsStringAsync()), Detail(await foreign.Content.ReadAsStringAsync()));
        Assert.Equal(Detail(await unknown.Content.ReadAsStringAsync()), Detail(await elsewhere.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
    }

    // A cursor from outside the window the person may read would tell them that the message exists, so it is as unknown as one that
    // does not.
    [Fact]
    public async Task A_cursor_from_before_the_person_joined_or_after_they_left_is_refused_like_an_unknown_one()
    {
        var owner = await _factory.RegisterUserAsync("owner");
        var late = await _factory.RegisterUserAsync("late");
        var leaver = await _factory.RegisterUserAsync("leaver");
        using var ownerClient = _factory.CreateClientFor(owner);
        using var lateClient = _factory.CreateClientFor(late);
        using var leaverClient = _factory.CreateClientFor(leaver);
        var group = (await ownerClient.CreateGroupAsync("Team", leaver.Id)).IdOf();
        var word = Unique();
        var early = await ownerClient.SendMessageAsync(group, $"{word} early");
        await ownerClient.AddMembersAsync(group, late.Id);
        (await leaverClient.LeaveResponseAsync(group)).EnsureSuccessStatusCode();
        var after = await ownerClient.SendMessageAsync(group, $"{word} after");

        var beforeJoining = await lateClient.SearchResponseAsync(word, before: early.MessageIdOf());
        var afterLeaving = await leaverClient.SearchResponseAsync(word, before: after.MessageIdOf());
        var unknown = await lateClient.SearchResponseAsync(word, before: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, beforeJoining.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, afterLeaving.StatusCode);
        Assert.Equal(Detail(await unknown.Content.ReadAsStringAsync()), Detail(await beforeJoining.Content.ReadAsStringAsync()));
        Assert.Equal(Detail(await unknown.Content.ReadAsStringAsync()), Detail(await afterLeaving.Content.ReadAsStringAsync()));

        // The owner may read both, so the same cursors are theirs to use.
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.SearchResponseAsync(word, before: early.MessageIdOf())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.SearchResponseAsync(word, before: after.MessageIdOf())).StatusCode);
    }

    // ##### Asking #####

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("  a  ")]
    public async Task Text_that_is_missing_or_shorter_than_two_characters_is_refused(string? typed)
    {
        var (alice, _, _) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.SearchResponseAsync(typed);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"Q\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Text_of_a_hundred_characters_is_searched_and_a_hundred_and_one_is_refused()
    {
        var (alice, _, _) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var longest = await client.SearchResponseAsync(new string('x', 100));
        var tooLong = await client.SearchResponseAsync(new string('x', 101));
        var paddedButShort = await client.SearchResponseAsync("  " + new string('x', 100) + "  ");

        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, paddedButShort.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_there_is_no_search()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.SearchResponseAsync("anything");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task What_was_searched_for_is_never_written_to_the_log()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        var word = Unique();
        await client.SendMessageAsync(conversation, $"hush {word}");

        Assert.Single(await client.SearchAsync(word));
        Assert.Empty(await client.SearchAsync($"{word}nothing"));

        Assert.DoesNotContain(capture.Entries, entry => entry.Contains(word));
        Assert.Contains(capture.Entries, entry => entry.Contains("searched and found"));
    }

    private static string Detail(string problem) => JsonDocument.Parse(problem).RootElement.GetProperty("detail").GetString()!;
}
