using System.Net;
using System.Text.Json;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Notifications;

// Naming someone with "@username" in a message notifies them. The notification is saved in the same transaction
// as the message, so there is never one without the other, and it is pushed to the person's open tabs afterwards.
[Collection(IntegrationTestCollection.Name)]
public class MentionNotificationTests
{
    private readonly ChatApiFactory _factory;

    public MentionNotificationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private Task<List<Notification>> NotificationsOfAsync(Guid conversation, Guid? user = null) =>
        _factory.QueryDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(n => n.ConversationId == conversation && (user == null || n.UserId == user))
            .OrderBy(n => n.CreatedAt)
            .ToListAsync());

    private async Task<(TestUser Alice, TestUser Bob, TestUser Carol, Guid Group)> GroupOfThreeAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);

        return (alice, bob, carol, (await client.CreateGroupAsync("Mentions", bob.Id, carol.Id)).IdOf());
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    // ##### Who is notified #####

    [Fact]
    public async Task A_mention_notifies_the_member_it_names_and_only_that_member()
    {
        var (alice, bob, carol, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);

        var message = await client.SendMessageAsync(group, $"hello @{bob.UserName}, are you there?");

        var mention = Assert.Single(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
        Assert.Equal(bob.Id, mention.UserId);
        Assert.Equal(alice.Id, mention.ActorId);
        Assert.Equal(group, mention.ConversationId);
        Assert.Equal(message.MessageIdOf(), mention.MessageId);
        Assert.False(mention.IsRead);
        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.UserId == carol.Id && n.Type == NotificationType.Mention);
    }

    [Fact]
    public async Task Everyone_a_message_names_is_notified_once_however_many_times_it_names_them()
    {
        var (alice, bob, carol, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(group, $"@{bob.UserName} and @{carol.UserName}: @{bob.UserName} please");

        var mentions = (await NotificationsOfAsync(group)).Where(n => n.Type == NotificationType.Mention).ToList();
        Assert.Equal(2, mentions.Count);
        Assert.Equal(new[] { bob.Id, carol.Id }.Order(), mentions.Select(n => n.UserId).Order());
    }

    [Fact]
    public async Task The_case_of_the_name_does_not_matter()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(group, $"@{bob.UserName.ToUpperInvariant()}");

        Assert.Single(await NotificationsOfAsync(group, bob.Id), n => n.Type == NotificationType.Mention);
    }

    [Fact]
    public async Task A_mention_works_in_a_direct_conversation_too()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(conversation, $"@{bob.UserName} look");

        Assert.Single(await NotificationsOfAsync(conversation, bob.Id));
    }

    [Fact]
    public async Task Naming_yourself_notifies_nobody()
    {
        var (alice, _, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(group, $"note to @{alice.UserName}");

        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
    }

    [Fact]
    public async Task A_person_who_is_not_in_the_conversation_is_not_notified_and_the_name_is_not_an_error()
    {
        var (alice, _, _, group) = await GroupOfThreeAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostMessageAsync(group, $"@{outsider.UserName} are you reading this?");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(await _factory.QueryDbAsync(db => db.Notifications.Where(n => n.UserId == outsider.Id).ToListAsync()));
    }

    [Fact]
    public async Task A_name_nobody_has_is_nothing()
    {
        var (alice, _, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostMessageAsync(group, "@nobody_has_this_name hello");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
    }

    [Fact]
    public async Task A_member_who_left_is_not_notified()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(group, $"@{bob.UserName}");

        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
    }

    [Fact]
    public async Task A_deactivated_account_is_not_notified()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        await _factory.DeactivateAsync(bob);
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(group, $"@{bob.UserName}");

        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
    }

    // Someone addressed the person by name, so muting the conversation does not hide it (D-194).
    [Fact]
    public async Task A_member_who_muted_the_conversation_is_still_notified_of_a_mention()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        using (var bobClient = _factory.CreateClientFor(bob))
            await bobClient.SetMutedAsync(group, true);
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(group, $"@{bob.UserName}");

        Assert.Single(await NotificationsOfAsync(group, bob.Id), n => n.Type == NotificationType.Mention);
    }

    [Theory]
    [InlineData("write to {name}@example.com")]
    [InlineData("just a plain message")]
    [InlineData("an at sign alone: @")]
    public async Task A_message_that_names_nobody_makes_no_notification(string template)
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.SendMessageAsync(group, template.Replace("{name}", bob.UserName));

        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
    }

    [Fact]
    public async Task A_message_of_only_files_makes_no_notification()
    {
        var (alice, _, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(group, new byte[10]);

        await client.SendMessageWithFilesAsync(group, null, (key, "a.png"));

        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
    }

    [Fact]
    public async Task Only_the_first_twenty_people_a_message_names_are_notified()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var others = new List<TestUser>();
        for (var i = 0; i < 22; i++)
            others.Add(await _factory.RegisterUserAsync("member"));
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Big", others.Select(o => o.Id).ToArray())).IdOf();

        await client.SendMessageAsync(group, string.Join(' ', others.Select(o => $"@{o.UserName}")));

        var mentions = (await NotificationsOfAsync(group)).Where(n => n.Type == NotificationType.Mention).ToList();
        Assert.Equal(20, mentions.Count);
        Assert.Equal(others.Take(20).Select(o => o.Id).Order(), mentions.Select(n => n.UserId).Order());
    }

    // ##### Saved with the message #####

    [Fact]
    public async Task A_send_that_loses_the_race_to_the_index_leaves_no_notification_behind()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        var clientMessageId = Guid.NewGuid();
        await _factory.AddMessageAsync(group, alice.Id, "winner", DateTime.UtcNow.AddMinutes(-1), clientMessageId);
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessageRepository>();
            services.AddScoped<IMessageRepository>(provider =>
                new ForgetfulMessageRepository(ActivatorUtilities.CreateInstance<MessageRepository>(provider)));
        }));
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        using var loser = Authorized(host, alice);

        var response = await loser.PostMessageAsync(group, $"@{bob.UserName} hello", clientMessageId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(await NotificationsOfAsync(group), n => n.Type == NotificationType.Mention);
        Assert.False(await bobsTab.ReceivedNotificationAsync(_ => true));
    }

    [Fact]
    public async Task Sending_the_same_message_again_does_not_notify_twice()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();

        await client.PostMessageAsync(group, $"@{bob.UserName}", clientMessageId);
        var again = await client.PostMessageAsync(group, $"@{bob.UserName}", clientMessageId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(await NotificationsOfAsync(group, bob.Id), n => n.Type == NotificationType.Mention);
    }

    // ##### The push #####

    [Fact]
    public async Task The_mentioned_persons_open_tabs_are_told_and_nobody_else_is()
    {
        var (alice, bob, carol, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        await using var bobsOtherTab = await HubClient.ConnectAsync(_factory, bob);
        await using var carolsTab = await HubClient.ConnectAsync(_factory, carol);
        await using var alicesTab = await HubClient.ConnectAsync(_factory, alice);

        var message = await client.SendMessageAsync(group, $"@{bob.UserName} come here");

        foreach (var tab in new[] { bobsTab, bobsOtherTab })
        {
            var pushed = await tab.WaitForNotificationAsync(n => n.GetProperty("conversationId").GetGuid() == group);
            Assert.Equal("Mention", pushed.GetProperty("type").GetString());
            Assert.Equal(alice.Id, pushed.GetProperty("actorId").GetGuid());
            Assert.Equal(alice.DisplayName, pushed.GetProperty("actorDisplayName").GetString());
            Assert.Equal(message.MessageIdOf(), pushed.GetProperty("messageId").GetGuid());
            Assert.Equal($"@{bob.UserName} come here", pushed.GetProperty("messagePreview").GetString());
            Assert.False(pushed.GetProperty("isRead").GetBoolean());
        }

        Assert.False(await carolsTab.ReceivedNotificationAsync(_ => true));
        Assert.False(await alicesTab.ReceivedNotificationAsync(_ => true));
    }

    [Fact]
    public async Task The_pushed_notification_is_the_one_that_was_saved()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);

        await client.SendMessageAsync(group, $"@{bob.UserName}");

        var pushed = await bobsTab.WaitForNotificationAsync(n => n.GetProperty("conversationId").GetGuid() == group);
        var saved = Assert.Single(await NotificationsOfAsync(group, bob.Id), n => n.Type == NotificationType.Mention);
        Assert.Equal(saved.Id, pushed.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_preview_in_the_push_is_cut_at_one_hundred_characters()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        var text = $"@{bob.UserName} " + new string('x', 200);

        await client.SendMessageAsync(group, text);

        var pushed = await bobsTab.WaitForNotificationAsync(n => n.GetProperty("conversationId").GetGuid() == group);
        Assert.Equal(text[..100], pushed.GetProperty("messagePreview").GetString());
    }

    [Fact]
    public async Task A_failing_push_does_not_fail_the_send_and_the_notification_is_still_saved()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.AspNetCore.SignalR.IHubContext<ChatNET.API.Messaging.Hubs.ChatHub, ChatNET.API.Messaging.Hubs.IChatClient>>();
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatNET.API.Messaging.Hubs.ChatHub, ChatNET.API.Messaging.Hubs.IChatClient>>(new ThrowingHubContext());
        }));
        using var client = Authorized(host, alice);

        var response = await client.PostMessageAsync(group, $"@{bob.UserName}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(await NotificationsOfAsync(group, bob.Id), n => n.Type == NotificationType.Mention);
    }

    // ##### Logging #####

    [Fact]
    public async Task The_log_counts_the_mentions_and_never_names_them_or_repeats_the_text()
    {
        var (alice, bob, _, group) = await GroupOfThreeAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);

        var message = await client.SendMessageAsync(group, $"@{bob.UserName} the secret plan");

        Assert.Contains(capture.Entries, e => e.Contains(message.MessageIdOf().ToString()) && e.Contains("and 1 mentions"));
        Assert.DoesNotContain(capture.Entries, e => e.Contains(bob.UserName) || e.Contains("secret plan"));
    }
}
