using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Media;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Media;

// Before a file is sent to the bucket the client asks the API for permission. The file is already encrypted, so the API is told only
// its size: it checks that the person may write in the conversation and that the size is one a sealed file can have, and answers with a
// link that is good for exactly that upload.
[Collection(IntegrationTestCollection.Name)]
public class UploadTicketTests
{
    private const string NotAMember = "You are not a member of this conversation.";

    private readonly ChatApiFactory _factory;

    public UploadTicketTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private long Limit => _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value.MaxFileBytes;

    // ##### A ticket #####

    [Fact]
    public async Task A_member_gets_a_key_in_their_own_folder_and_a_link_that_expires_in_ten_minutes()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var response = await client.RequestUploadAsync(conversation, 1234);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        var key = ticket.GetProperty("uploadKey").GetString()!;
        Assert.True(AttachmentKeys.IsUploadKeyFor(key, conversation, alice.Id));
        Assert.StartsWith(_factory.StorageServiceUrl + "/" + ChatApiFactory.StorageBucket + "/" + key, ticket.GetProperty("url").GetString());
        ticket.AssertExpiryIsWholeSecondsUtc(minutes: 10);
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

    // The bucket is told nothing about the file: whatever the bytes are, the link takes them with one content type.
    [Fact]
    public async Task The_link_accepts_the_file_with_the_one_content_type_it_was_signed_for_and_nothing_else()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var ticket = await client.GetUploadTicketAsync(conversation, 50);
        var key = ticket.GetProperty("uploadKey").GetString()!;

        var wrongType = await MediaApi.PutToTicketAsync(ticket, new byte[50], "image/png");
        Assert.False(wrongType.IsSuccessStatusCode);
        Assert.False(await _factory.ObjectExistsAsync(key));

        var right = await MediaApi.PutToTicketAsync(ticket, new byte[50], "application/octet-stream");
        Assert.True(right.IsSuccessStatusCode);
        Assert.True(await _factory.ObjectExistsAsync(key));
    }

    // A client that still sends a name or a type is not refused for it, and neither is read.
    [Fact]
    public async Task A_name_or_a_type_sent_with_the_size_is_ignored()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/uploads",
            new { fileName = "my secret tax return.pdf", contentType = "application/pdf", sizeBytes = 100 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        var key = ticket.GetProperty("uploadKey").GetString()!;
        Assert.DoesNotContain("tax", key);
        Assert.DoesNotContain("tax", ticket.GetProperty("url").GetString());
        Assert.True((await MediaApi.PutToTicketAsync(ticket, new byte[100])).IsSuccessStatusCode);
    }

    // ##### Who may ask #####

    [Fact]
    public async Task Someone_who_is_not_a_member_is_refused_with_the_same_answer_as_for_an_unknown_conversation()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var client = _factory.CreateClientFor(stranger);

        var notMember = await client.RequestUploadAsync(conversation, 100);
        var unknown = await client.RequestUploadAsync(Guid.NewGuid(), 100);

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

        var response = await client.RequestUploadAsync(conversation, 100);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(alice);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused_even_with_a_token_that_is_still_valid()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.DeactivateAsync(alice);

        var response = await client.RequestUploadAsync(conversation, 100);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_answer_is_401()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.RequestUploadAsync(conversation, 100);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### What may be uploaded #####

    // A file of the limit becomes the file, a header and a tag for each chunk of 64 KiB: 10 MiB are 160 chunks.
    [Fact]
    public async Task The_largest_object_for_a_file_of_the_limit_is_accepted_and_one_byte_more_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var largest = SealedFile.MaxBytes(Limit);

        var atLimit = await client.RequestUploadAsync(conversation, largest);
        var over = await client.RequestUploadAsync(conversation, largest + 1);

        Assert.Equal(Limit + 13 + 160 * 16, largest);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.True((await over.ReadProblemAsync()).Errors!.Value.TryGetProperty("SizeBytes", out _));
    }

    [Fact]
    public async Task The_smallest_sealed_file_is_accepted_and_one_byte_less_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var smallest = await client.RequestUploadAsync(conversation, SealedFile.MinBytes);
        var smaller = await client.RequestUploadAsync(conversation, SealedFile.MinBytes - 1);

        Assert.Equal(HttpStatusCode.OK, smallest.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, smaller.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task An_empty_or_negative_size_is_refused(long size)
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RequestUploadAsync(conversation, size);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_no_size_is_refused()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/uploads", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/uploads",
            new { fileName = "my secret tax return.pdf", contentType = "application/pdf", sizeBytes = 100 });
        response.EnsureSuccessStatusCode();

        Assert.Contains(capture.Entries, e => e.Contains(conversation.ToString()) && e.Contains(alice.Id.ToString()));
        Assert.DoesNotContain(capture.Entries, e => e.Contains("tax return"));
    }
}
