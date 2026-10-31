using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Messaging;

// Sending is a REST call, but everyone else in the conversation hears about it over SignalR. These
// tests send through the real endpoint and watch real hub connections, as browsers would.
[Collection(IntegrationTestCollection.Name)]
public class MessageDeliveryTests
{
    private readonly ChatApiFactory _factory;

    public MessageDeliveryTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### Who receives the push #####

    [Fact]
    public async Task The_other_member_receives_exactly_what_the_sender_got_back()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        var sent = await aliceApi.SendMessageAsync(conversation, "Hi Bob", Guid.NewGuid());

        var pushed = await bobClient.WaitForMessageAsync(conversation, sent.MessageIdOf());
        // Compared as JSON text: the push and the response must be the same message, field for field.
        Assert.Equal(sent.GetRawText(), pushed.GetRawText());
    }

    // The sender is in the group too. Their sending tab uses the push to confirm its optimistic
    // copy; their other tabs have no other way to learn about the message.
    [Fact]
    public async Task The_senders_own_tabs_receive_it_too_with_the_client_message_id()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await using var sendingTab = await HubClient.ConnectAsync(_factory, alice);
        await using var otherTab = await HubClient.ConnectAsync(_factory, alice);
        var clientMessageId = Guid.NewGuid();

        var sent = await aliceApi.SendMessageAsync(conversation, "From my laptop", clientMessageId);

        foreach (var tab in new[] { sendingTab, otherTab })
        {
            var pushed = await tab.WaitForMessageAsync(conversation, sent.MessageIdOf());
            Assert.Equal(clientMessageId, pushed.GetProperty("clientMessageId").GetGuid());
        }
    }

    [Fact]
    public async Task Every_online_member_of_a_group_receives_it_and_nobody_else_does()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var aliceApi = _factory.CreateClientFor(alice);
        var group = (await aliceApi.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();
        var unrelated = (await aliceApi.CreateDirectAsync(outsider.Id)).IdOf();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var carolClient = await HubClient.ConnectAsync(_factory, carol);
        await using var outsiderClient = await HubClient.ConnectAsync(_factory, outsider);

        var sent = await aliceApi.SendMessageAsync(group, "Hello team");

        await bobClient.WaitForMessageAsync(group, sent.MessageIdOf());
        await carolClient.WaitForMessageAsync(group, sent.MessageIdOf());
        Assert.False(await outsiderClient.ReceivedMessageAsync(group));
        Assert.DoesNotContain(outsiderClient.Messages, m => m.GetProperty("conversationId").GetGuid() == unrelated);
    }

    [Fact]
    public async Task A_member_who_is_offline_causes_no_error()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();

        var response = await aliceApi.PostMessageAsync(conversation, "Anyone there?");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Messages_sent_one_after_another_arrive_in_the_order_they_were_sent()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        var sentIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
            sentIds.Add((await aliceApi.SendMessageAsync(conversation, $"message {i}")).MessageIdOf());
        await bobClient.WaitForMessageAsync(conversation, sentIds[^1]);

        var receivedIds = bobClient.Messages.Select(m => m.MessageIdOf()).ToList();
        Assert.Equal(sentIds, receivedIds);
    }

    // Two application instances behind a load balancer: the sender's request lands on one and the
    // recipient's connection is on the other. Only the Redis backplane carries the push across.
    [Fact]
    public async Task A_member_connected_to_another_server_instance_receives_it()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await using var bobClient = await HubClient.ConnectAsync(otherInstance, bob);

        var sent = await aliceApi.SendMessageAsync(conversation, "Across instances");

        await bobClient.WaitForMessageAsync(conversation, sent.MessageIdOf());
    }

    // ##### Only successful sends are announced #####

    [Fact]
    public async Task A_send_that_is_refused_pushes_nothing()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var intruder = await _factory.RegisterUserAsync("intruder");
        using var aliceApi = _factory.CreateClientFor(alice);
        using var intruderApi = _factory.CreateClientFor(intruder);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        var invalid = await aliceApi.PostMessageAsync(conversation, "   ");
        var forbidden = await intruderApi.PostMessageAsync(conversation, "I should not be here");

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.False(await bobClient.ReceivedMessageAsync(conversation));
    }

    // Clients treat a push as proof that the message exists. If it were sent before the commit, a
    // failed save would leave them showing a message that was never stored. The spy looks in the
    // database at the instant the push is made.
    [Fact]
    public async Task The_message_is_already_stored_when_the_push_is_made()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        var seenAtPush = new List<(string Group, bool Stored)>();
        using var spyHost = WithHubContext(new SpyHubContext(async (group, message) =>
        {
            var stored = await _factory.QueryDbAsync(db => db.Messages.AnyAsync(m => m.Id == message.Id));
            seenAtPush.Add((group, stored));
        }));
        using var client = ClientFor(spyHost, alice);

        await client.SendMessageAsync(conversation, "Check the database");

        var push = Assert.Single(seenAtPush);
        Assert.Equal(ChatGroups.Conversation(conversation), push.Group);
        Assert.True(push.Stored, "The message was pushed before it was saved.");
    }

    // ##### Failure of the push #####

    // The message is saved before the push is attempted, so a failing push (Redis down, for
    // example) must not turn a successful send into an error: the client would retry and send a
    // duplicate. The row saved with the message stays undispatched and the relay makes the push.
    [Fact]
    public async Task A_failing_push_does_not_fail_the_send_and_is_logged_without_the_content()
    {
        var capture = new CapturingLoggerProvider();
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceApi = _factory.CreateClientFor(alice);
        var conversation = (await aliceApi.CreateDirectAsync(bob.Id)).IdOf();
        using var brokenHub = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
            services.AddLogging(logging => logging.AddProvider(capture));
        }));
        using var client = ClientFor(brokenHub, alice);
        var secret = $"secret-{Guid.NewGuid():N}";

        var response = await client.PostMessageAsync(conversation, secret);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await response.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await _factory.QueryDbAsync(db => db.Messages.SingleAsync(m => m.Id == sent.MessageIdOf()));
        Assert.Equal(secret, stored.Content);
        // The warning names the outbox row that stands for the push, which the relay will retry.
        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains(row.Id.ToString()));
        Assert.DoesNotContain(secret, warning);
        Assert.Null(row.DispatchedAt);
        Assert.DoesNotContain(capture.Entries, e => e.Contains(secret));
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithHubContext(SpyHubContext spy) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub, IChatClient>>(spy);
        }));

    private static HttpClient ClientFor(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }
}
