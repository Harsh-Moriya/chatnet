using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Messaging;

[Collection(IntegrationTestCollection.Name)]
public class SendMessageTests
{
    private const string NotAMember = "You are not a member of this conversation.";

    private readonly ChatApiFactory _factory;

    public SendMessageTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### What a successful send returns #####

    [Fact]
    public async Task A_member_can_send_and_gets_the_saved_message_back()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var before = DateTime.UtcNow;

        var response = await client.PostMessageAsync(conversation, "Hello Bob");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, message.MessageIdOf());
        Assert.Equal(conversation, message.GetProperty("conversationId").GetGuid());
        Assert.Equal(alice.Id, message.GetProperty("senderId").GetGuid());
        Assert.Equal(alice.DisplayName, message.GetProperty("senderDisplayName").GetString());
        Assert.Equal("Hello Bob", message.ContentOf());
        Assert.InRange(message.GetProperty("createdAt").GetDateTime(), before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(JsonValueKind.Null, message.GetProperty("clientMessageId").ValueKind);
    }

    [Fact]
    public async Task The_message_is_stored_against_the_conversation_and_the_token_owner()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        var sent = await client.SendMessageAsync(conversation, "Remember me");

        var stored = await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == sent.MessageIdOf()));
        Assert.Equal(conversation, stored.ConversationId);
        Assert.Equal(alice.Id, stored.SenderId);
        Assert.Equal("Remember me", stored.Content);
        Assert.False(stored.IsDeleted);
        Assert.Equal(0, stored.EditCount);
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), stored.CreatedAt);
    }

    [Fact]
    public async Task The_client_message_id_is_echoed_back_but_is_not_the_message_id()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var clientMessageId = Guid.NewGuid();

        var sent = await client.SendMessageAsync(conversation, "Optimistic", clientMessageId);

        Assert.Equal(clientMessageId, sent.GetProperty("clientMessageId").GetGuid());
        Assert.NotEqual(clientMessageId, sent.MessageIdOf());
    }

    [Fact]
    public async Task Surrounding_whitespace_is_removed_but_inner_whitespace_is_kept()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        var sent = await client.SendMessageAsync(conversation, "  line one\n\nline  three \t\n");

        Assert.Equal("line one\n\nline  three", sent.ContentOf());
    }

    [Fact]
    public async Task Unicode_and_markup_survive_unchanged()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        const string content = "नमस्ते 👋 <script>alert(1)</script> & \"quotes\" 'single' 日本語";

        var sent = await client.SendMessageAsync(conversation, content);

        Assert.Equal(content, sent.ContentOf());
        var stored = await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == sent.MessageIdOf()));
        Assert.Equal(content, stored.Content);
    }

    [Fact]
    public async Task Any_member_of_a_group_can_send()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        var sent = await carolClient.SendMessageAsync(group, "From the last member");

        Assert.Equal(carol.Id, sent.GetProperty("senderId").GetGuid());
        Assert.Equal(carol.DisplayName, sent.GetProperty("senderDisplayName").GetString());
    }

    // ##### Effect on the conversation #####

    // Conversations are listed by UpdatedAt, newest first, so sending must move the conversation to
    // the top of every member's list and show the message as the preview.
    [Fact]
    public async Task Sending_moves_the_conversation_to_the_top_of_every_members_list()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var older = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        var newer = (await aliceClient.CreateDirectAsync(carol.Id)).IdOf();
        Assert.Equal(newer, (await aliceClient.ListConversationsAsync()).First().IdOf());

        var sent = await aliceClient.SendMessageAsync(older, "Bump");

        foreach (var client in new[] { aliceClient, bobClient })
        {
            var first = (await client.ListConversationsAsync()).First();
            Assert.Equal(older, first.IdOf());
            Assert.Equal("Bump", first.GetProperty("lastMessage").GetProperty("content").GetString());
        }

        var updatedAt = await _factory.QueryDbAsync(db =>
            db.Conversations.Where(c => c.Id == older).Select(c => c.UpdatedAt).SingleAsync());
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), updatedAt);
    }

    [Fact]
    public async Task Sending_in_one_conversation_leaves_the_others_untouched()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var withBob = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var withCarol = (await client.CreateDirectAsync(carol.Id)).IdOf();
        var carolUpdatedAt = await UpdatedAtAsync(withCarol);

        await client.SendMessageAsync(withBob, "Only for Bob");

        Assert.Equal(carolUpdatedAt, await UpdatedAtAsync(withCarol));
        Assert.Empty(await _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == withCarol).ToListAsync()));
    }

    // ##### Who may send #####

    [Fact]
    public async Task A_non_member_is_refused_and_nothing_is_stored()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var intruderClient = _factory.CreateClientFor(intruder);
        var conversation = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        var updatedAtBefore = await UpdatedAtAsync(conversation);

        var response = await intruderClient.PostMessageAsync(conversation, "Let me in");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Forbidden", problem.Title);
        Assert.Equal(NotAMember, problem.Detail);
        Assert.Empty(await _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == conversation).ToListAsync()));
        Assert.Equal(updatedAtBefore, await UpdatedAtAsync(conversation));
    }

    // Same rule as the hub: a different answer for "does not exist" and "not yours" would let anyone
    // find out which conversation IDs are real.
    [Fact]
    public async Task An_unknown_conversation_gets_the_same_refusal_as_one_the_user_is_not_in()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var intruderClient = _factory.CreateClientFor(intruder);
        var existing = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();

        var forExisting = await intruderClient.PostMessageAsync(existing, "hi");
        var forUnknown = await intruderClient.PostMessageAsync(Guid.NewGuid(), "hi");

        Assert.Equal(HttpStatusCode.Forbidden, forExisting.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forUnknown.StatusCode);
        Assert.Equal((await forExisting.ReadProblemAsync()).Detail, (await forUnknown.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_member_who_has_left_can_no_longer_send()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var group = (await aliceClient.CreateGroupAsync("Leaving", bob.Id)).IdOf();
        await LeaveAsync(group, bob);

        var response = await bobClient.PostMessageAsync(group, "Still here?");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == group).ToListAsync()));
    }

    // A token stays valid until it expires, so a deactivated account can still present one.
    // Sending is the one place that writes content other people read, so it is refused here.
    [Fact]
    public async Task A_deactivated_user_cannot_send_even_with_a_token_that_has_not_expired()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        var conversation = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        await _factory.DeactivateAsync(alice);

        var response = await aliceClient.PostMessageAsync(conversation, "From beyond");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == conversation).ToListAsync()));
    }

    [Fact]
    public async Task Sending_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostMessageAsync(Guid.NewGuid(), "anonymous");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await response.ReadProblemAsync()).Status);
    }

    // The sender comes from the token. A client that sends a sender in the body cannot speak as
    // someone else.
    [Fact]
    public async Task A_sender_id_in_the_body_is_ignored()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { content = "Forged", senderId = bob.Id });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(alice.Id, sent.GetProperty("senderId").GetGuid());
    }

    // ##### Validation #####

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t \r\n")]
    public async Task Content_that_is_missing_or_blank_is_rejected(string? content)
    {
        using var chat = await ConversationForAliceAsync();
        var (client, conversation) = (chat.Client, chat.ConversationId);

        var response = await client.PostMessageAsync(conversation, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Validation Failed", problem.Title);
        Assert.True(problem.Errors!.Value.GetProperty("Content").GetArrayLength() > 0);
        Assert.Empty(await _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == conversation).ToListAsync()));
    }

    [Fact]
    public async Task Content_of_exactly_4000_characters_is_accepted()
    {
        using var chat = await ConversationForAliceAsync();
        var (client, conversation) = (chat.Client, chat.ConversationId);

        var sent = await client.SendMessageAsync(conversation, new string('a', 4000));

        Assert.Equal(4000, sent.ContentOf()!.Length);
    }

    [Fact]
    public async Task Content_over_4000_characters_is_rejected()
    {
        using var chat = await ConversationForAliceAsync();
        var (client, conversation) = (chat.Client, chat.ConversationId);

        var response = await client.PostMessageAsync(conversation, new string('a', 4001));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("4000", problem.Detail);
        Assert.Empty(await _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == conversation).ToListAsync()));
    }

    [Fact]
    public async Task An_empty_client_message_id_is_rejected()
    {
        using var chat = await ConversationForAliceAsync();
        var (client, conversation) = (chat.Client, chat.ConversationId);

        var response = await client.PostMessageAsync(conversation, "hi", Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync()).Errors!.Value.TryGetProperty("ClientMessageId", out _));
    }

    [Fact]
    public async Task A_conversation_id_that_is_not_a_guid_does_not_match_the_route()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostAsJsonAsync("/api/conversations/not-a-guid/messages", new { content = "hi" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, (await response.ReadProblemAsync()).Status);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_400_problem()
    {
        using var chat = await ConversationForAliceAsync();
        var (client, conversation) = (chat.Client, chat.ConversationId);

        var response = await client.PostAsync($"/api/conversations/{conversation}/messages",
            new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await response.ReadProblemAsync()).Status);
    }

    // ##### Cost and concurrency #####

    // Membership check, sender lookup, and one write (message insert plus conversation update in a
    // single round trip). The count must not depend on how many people are in the conversation.
    [Fact]
    public async Task Sending_uses_a_fixed_number_of_queries_however_large_the_group_is()
    {
        var counter = new QueryCounter();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(counter))));
        var alice = await _factory.RegisterUserAsync("alice");
        var members = new List<Guid>();
        for (var i = 0; i < 12; i++)
            members.Add((await _factory.RegisterUserAsync("member")).Id);
        using var setup = _factory.CreateClientFor(alice);
        var group = (await setup.CreateGroupAsync("Big", members.ToArray())).IdOf();

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        counter.Reset();
        var response = await client.PostMessageAsync(group, "Count me");
        response.EnsureSuccessStatusCode();

        Assert.True(counter.Count <= 3, $"Sending ran {counter.Count} queries; expected at most 3.");
    }

    // Everyone in a busy conversation updates the same conversation row. The writes queue up on that
    // row's lock; none may fail, deadlock, or be lost.
    [Fact]
    public async Task Many_members_sending_at_once_all_succeed_and_are_all_stored()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        using var carolClient = _factory.CreateClientFor(carol);
        var group = (await aliceClient.CreateGroupAsync("Busy", bob.Id, carol.Id)).IdOf();

        var clients = new[] { aliceClient, bobClient, carolClient };
        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            clients[i % 3].PostMessageAsync(group, $"message {i}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var stored = await _factory.QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == group).ToListAsync());
        Assert.Equal(30, stored.Count);
        Assert.Equal(30, stored.Select(m => m.Id).Distinct().Count());
        var updatedAt = await UpdatedAtAsync(group);
        Assert.InRange(updatedAt, stored.Min(m => m.CreatedAt), stored.Max(m => m.CreatedAt));
    }

    // ##### Logging #####

    // Message content is private. The log may say that a message was sent and by whom, never what
    // it said.
    [Fact]
    public async Task The_content_of_a_message_is_never_logged()
    {
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddLogging(logging => logging.AddProvider(capture))));
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        var secret = $"secret-{Guid.NewGuid():N}";

        var sent = await client.SendMessageAsync(conversation, secret);
        var rejected = await client.PostMessageAsync(Guid.NewGuid(), secret);

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.DoesNotContain(capture.Entries, entry => entry.Contains(secret));
        // Without this the test would also pass if nothing were logged or captured at all.
        Assert.Contains(capture.Entries, entry => entry.Contains(sent.MessageIdOf().ToString()));
    }

    private async Task<AliceInAConversation> ConversationForAliceAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var client = _factory.CreateClientFor(alice);
        return new AliceInAConversation(client, (await client.CreateDirectAsync(bob.Id)).IdOf());
    }

    private sealed record AliceInAConversation(HttpClient Client, Guid ConversationId) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private Task<DateTime> UpdatedAtAsync(Guid conversationId) =>
        _factory.QueryDbAsync(db =>
            db.Conversations.Where(c => c.Id == conversationId).Select(c => c.UpdatedAt).SingleAsync());

    private Task LeaveAsync(Guid conversationId, TestUser user) =>
        _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
}
