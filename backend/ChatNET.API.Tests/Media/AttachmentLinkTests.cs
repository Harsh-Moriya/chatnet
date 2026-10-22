using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Media;

// The link that comes with a message works for an hour. A client that kept a message open for longer asks for a
// new one, and gets it under the same rule that decides who can read the message at all.
[Collection(IntegrationTestCollection.Name)]
public class AttachmentLinkTests
{
    private readonly ChatApiFactory _factory;

    public AttachmentLinkTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    // A message from someone, with one real file, sent through the API.
    private async Task<(TestUser Alice, TestUser Bob, Guid Conversation, Guid Attachment, byte[] Content)> SentFileAsync(
        string contentType = "image/png", string fileName = "photo.png")
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var content = Bytes(150);
        var key = await client.UploadFileAsync(conversation, content, contentType, fileName);
        var message = await client.SendMessageWithFilesAsync(conversation, null, (key, fileName));

        return (alice, bob, conversation, Assert.Single(message.AttachmentsOf()).IdOf(), content);
    }

    [Fact]
    public async Task A_member_gets_a_new_link_that_downloads_the_file_and_lasts_an_hour()
    {
        var (_, bob, _, attachment, content) = await SentFileAsync();
        using var client = _factory.CreateClientFor(bob);
        var before = DateTimeOffset.UtcNow;

        var response = await client.GetAttachmentLinkResponseAsync(attachment);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var link = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(
            link.GetProperty("expiresAt").GetDateTimeOffset(),
            before.AddMinutes(60).AddSeconds(-5),
            DateTimeOffset.UtcNow.AddMinutes(60).AddSeconds(5));
        var download = await new HttpClient().GetAsync(link.GetProperty("url").GetString());
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_sender_can_ask_too()
    {
        var (alice, _, _, attachment, _) = await SentFileAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAttachmentLinkResponseAsync(attachment);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_picture_is_offered_to_be_shown_and_any_other_file_to_be_downloaded()
    {
        var picture = await SentFileAsync("image/png", "photo.png");
        var document = await SentFileAsync("application/pdf", "plan.pdf");
        using var pictureReader = _factory.CreateClientFor(picture.Bob);
        using var documentReader = _factory.CreateClientFor(document.Bob);

        var pictureLink = await (await pictureReader.GetAttachmentLinkResponseAsync(picture.Attachment))
            .Content.ReadFromJsonAsync<JsonElement>();
        var documentLink = await (await documentReader.GetAttachmentLinkResponseAsync(document.Attachment))
            .Content.ReadFromJsonAsync<JsonElement>();

        var shown = await new HttpClient().GetAsync(pictureLink.GetProperty("url").GetString());
        var downloaded = await new HttpClient().GetAsync(documentLink.GetProperty("url").GetString());
        Assert.Equal("inline", shown.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("plan.pdf", downloaded.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    // ##### Who may ask #####

    [Fact]
    public async Task Someone_outside_the_conversation_and_an_unknown_id_get_the_same_answer()
    {
        var (_, _, _, attachment, _) = await SentFileAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = _factory.CreateClientFor(stranger);

        var outsider = await client.GetAttachmentLinkResponseAsync(attachment);
        var unknown = await client.GetAttachmentLinkResponseAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var outsiderProblem = await outsider.ReadProblemAsync();
        var unknownProblem = await unknown.ReadProblemAsync();
        Assert.Equal(unknownProblem.Title, outsiderProblem.Title);
        Assert.StartsWith("Attachment with ID", outsiderProblem.Detail);
        Assert.StartsWith("Attachment with ID", unknownProblem.Detail);
    }

    [Fact]
    public async Task A_member_who_has_left_can_no_longer_ask()
    {
        var (_, bob, conversation, attachment, _) = await SentFileAsync();
        await _factory.ExecuteDbAsync(async db =>
        {
            db.ConversationMembers.Single(m => m.ConversationId == conversation && m.UserId == bob.Id).LeftAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });
        using var client = _factory.CreateClientFor(bob);

        var response = await client.GetAttachmentLinkResponseAsync(attachment);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The rule for history: what was sent before a member joined is not theirs to read, and a file is no different.
    [Fact]
    public async Task A_file_sent_before_the_member_joined_is_not_theirs_to_ask_for()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var (_, attachments) = await _factory.AddMessageWithFilesAsync(
            conversation, alice.Id, "", DateTime.UtcNow.AddHours(-1), fileCount: 1);
        using var client = _factory.CreateClientFor(bob);

        var before = await client.GetAttachmentLinkResponseAsync(attachments[0]);
        await _factory.BackdateJoinAsync(conversation);
        var after = await client.GetAttachmentLinkResponseAsync(attachments[0]);

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_file_of_a_deleted_message_is_gone_for_everyone()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.BackdateJoinAsync(conversation);
        var (_, attachments) = await _factory.AddMessageWithFilesAsync(
            conversation, alice.Id, "", DateTime.UtcNow.AddHours(-1), fileCount: 1, deleted: true);
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        Assert.Equal(HttpStatusCode.NotFound, (await aliceClient.GetAttachmentLinkResponseAsync(attachments[0])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAttachmentLinkResponseAsync(attachments[0])).StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_answer_is_401()
    {
        var (_, _, _, attachment, _) = await SentFileAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAttachmentLinkResponseAsync(attachment);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_does_not_match_the_route()
    {
        var (alice, _, _) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync("/api/attachments/not-a-guid/url");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ##### Logging #####

    [Fact]
    public async Task A_refused_request_is_logged_with_ids_only()
    {
        var (_, _, _, attachment, _) = await SentFileAsync("application/pdf", "private contract.pdf");
        var stranger = await _factory.RegisterUserAsync("stranger");
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", stranger.Token);

        await client.GetAttachmentLinkResponseAsync(attachment);

        var line = Assert.Single(capture.Entries, e => e.Contains("asked for a link to attachment"));
        Assert.StartsWith("[Warning]", line);
        Assert.Contains(attachment.ToString(), line);
        Assert.Contains(stranger.Id.ToString(), line);
        Assert.DoesNotContain(capture.Entries, e => e.Contains("private contract"));
    }
}
