using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Media;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Media;

// Before a file is sent to the bucket the client asks the API for permission. The API checks that the person
// may write in the conversation and that the file is of an allowed type and size, and answers with a link that
// is good for exactly that upload.
[Collection(IntegrationTestCollection.Name)]
public class UploadTicketTests
{
    private const string NotAMember = "You are not a member of this conversation.";

    private readonly ChatApiFactory _factory;

    public UploadTicketTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### A ticket #####

    [Fact]
    public async Task A_member_gets_a_key_in_their_own_folder_and_a_link_that_expires_in_ten_minutes()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var before = DateTimeOffset.UtcNow;

        var response = await client.RequestUploadAsync(conversation, "photo.png", "image/png", 1234);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        var key = ticket.GetProperty("uploadKey").GetString()!;
        Assert.True(AttachmentKeys.IsUploadKeyFor(key, conversation, alice.Id));
        Assert.StartsWith(_factory.StorageServiceUrl + "/" + ChatApiFactory.StorageBucket + "/" + key, ticket.GetProperty("url").GetString());
        Assert.InRange(
            ticket.GetProperty("expiresAt").GetDateTimeOffset(),
            before.AddMinutes(10).AddSeconds(-5),
            DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(5));
    }

    [Fact]
    public async Task Each_ticket_has_a_key_of_its_own()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var first = await client.GetUploadTicketAsync(conversation);
        var second = await client.GetUploadTicketAsync(conversation);

        Assert.NotEqual(first.GetProperty("uploadKey").GetString(), second.GetProperty("uploadKey").GetString());
    }

    [Fact]
    public async Task The_link_accepts_the_file_with_the_content_type_that_was_asked_for_and_nothing_else()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation, "photo.png", "image/png", 50);
        var key = ticket.GetProperty("uploadKey").GetString()!;

        var wrongType = await MediaApi.PutToTicketAsync(ticket, new byte[50], "application/x-msdownload");
        Assert.False(wrongType.IsSuccessStatusCode);
        Assert.False(await _factory.ObjectExistsAsync(key));

        var right = await MediaApi.PutToTicketAsync(ticket, new byte[50], "image/png");
        Assert.True(right.IsSuccessStatusCode);
        Assert.True(await _factory.ObjectExistsAsync(key));
    }

    [Theory]
    [InlineData("IMAGE/PNG")]
    [InlineData("image/png; charset=binary")]
    public async Task The_type_is_matched_without_case_or_parameters_and_signed_in_its_plain_form(string asked)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation, "photo.png", asked, 50);

        var put = await MediaApi.PutToTicketAsync(ticket, new byte[50], "image/png");

        Assert.True(put.IsSuccessStatusCode);
    }

    // ##### Who may ask #####

    [Fact]
    public async Task Someone_who_is_not_a_member_is_refused_with_the_same_answer_as_for_an_unknown_conversation()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = _factory.CreateClientFor(stranger);

        var notMember = await client.RequestUploadAsync(conversation, "a.png", "image/png", 10);
        var unknown = await client.RequestUploadAsync(Guid.NewGuid(), "a.png", "image/png", 10);

        Assert.Equal(HttpStatusCode.Forbidden, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(NotAMember, (await notMember.ReadProblemAsync()).Detail);
        Assert.Equal(NotAMember, (await unknown.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task A_member_who_has_left_is_refused()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await _factory.ExecuteDbAsync(async db =>
        {
            var membership = db.ConversationMembers.Single(m => m.ConversationId == conversation && m.UserId == bob.Id);
            membership.LeftAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });
        using var client = _factory.CreateClientFor(bob);

        var response = await client.RequestUploadAsync(conversation, "a.png", "image/png", 10);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(alice);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused_even_with_a_token_that_is_still_valid()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.DeactivateAsync(alice);

        var response = await client.RequestUploadAsync(conversation, "a.png", "image/png", 10);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_answer_is_401()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.RequestUploadAsync(conversation, "a.png", "image/png", 10);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### What may be uploaded #####

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/x-msdownload")]
    [InlineData("application/javascript")]
    [InlineData("image")]
    public async Task A_type_that_is_not_allowed_is_refused(string contentType)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RequestUploadAsync(conversation, "file.bin", contentType, 100);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync()).Errors!.Value.TryGetProperty("ContentType", out _));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    [InlineData("application/zip")]
    public async Task Every_configured_type_is_accepted(string contentType)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RequestUploadAsync(conversation, "file.bin", contentType, 100);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_file_of_exactly_the_size_limit_is_accepted_and_one_byte_more_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var limit = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value.MaxFileBytes;

        var atLimit = await client.RequestUploadAsync(conversation, "a.png", "image/png", limit);
        var over = await client.RequestUploadAsync(conversation, "a.png", "image/png", limit + 1);

        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.True((await over.ReadProblemAsync()).Errors!.Value.TryGetProperty("SizeBytes", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task An_empty_or_negative_size_is_refused(long size)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RequestUploadAsync(conversation, "a.png", "image/png", size);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_file_name_is_refused(string? name)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RequestUploadAsync(conversation, name, "image/png", 100);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync()).Errors!.Value.TryGetProperty("FileName", out _));
    }

    [Fact]
    public async Task A_file_name_longer_than_255_characters_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RequestUploadAsync(conversation, new string('a', 256), "image/png", 100);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_content_type_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RequestUploadAsync(conversation, "a.png", null, 100);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync()).Errors!.Value.TryGetProperty("ContentType", out _));
    }

    // ##### Logging #####

    [Fact]
    public async Task The_log_names_the_conversation_and_the_person_and_never_the_file()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);

        var response = await client.RequestUploadAsync(conversation, "my secret tax return.pdf", "application/pdf", 100);
        response.EnsureSuccessStatusCode();

        Assert.Contains(capture.Entries, e => e.Contains(conversation.ToString()) && e.Contains(alice.Id.ToString()));
        Assert.DoesNotContain(capture.Entries, e => e.Contains("tax return"));
    }
}
