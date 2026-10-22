using System.Net;
using System.Net.Http.Headers;
using ChatNET.API.Media;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Media;

// The storage wrapper against a real S3-compatible server: the links it signs have to work from a plain HTTP
// client, because that is all a browser is to the bucket.
[Collection(IntegrationTestCollection.Name)]
public class BlobStorageTests
{
    private static readonly HttpClient Http = new();

    private readonly ChatApiFactory _factory;
    private readonly IBlobStorage _storage;

    public BlobStorageTests(ChatApiFactory factory)
    {
        _factory = factory;
        _storage = factory.Services.GetRequiredService<IBlobStorage>();
    }

    private static string NewKey(string folder = "pending") => $"{folder}/{Guid.NewGuid()}/{Guid.NewGuid()}";

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static async Task<HttpResponseMessage> PutAsync(string url, byte[] content, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await Http.SendAsync(request);
    }

    // ##### Uploading #####

    [Fact]
    public async Task A_signed_upload_link_accepts_the_file_with_the_signed_content_type()
    {
        var key = NewKey();
        var content = Bytes(2048);
        var ticket = _storage.CreateUploadUrl(key, "image/png");

        var response = await PutAsync(ticket.Url, content, "image/png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await _factory.GetObjectBytesAsync(key));
    }

    [Fact]
    public async Task A_signed_upload_link_refuses_a_file_that_claims_another_content_type()
    {
        var key = NewKey();
        var ticket = _storage.CreateUploadUrl(key, "image/png");

        var response = await PutAsync(ticket.Url, Bytes(100), "application/x-msdownload");

        Assert.False(response.IsSuccessStatusCode);
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task A_signed_upload_link_is_for_one_key_only()
    {
        var key = NewKey();
        var otherKey = NewKey();
        var tampered = _storage.CreateUploadUrl(key, "image/png").Url.Replace(key, otherKey);

        var response = await PutAsync(tampered, Bytes(100), "image/png");

        Assert.False(response.IsSuccessStatusCode);
        Assert.False(await _factory.ObjectExistsAsync(otherKey));
    }

    [Fact]
    public void An_upload_link_points_at_the_configured_endpoint_and_lasts_the_configured_time()
    {
        var before = DateTimeOffset.UtcNow;

        var ticket = _storage.CreateUploadUrl(NewKey(), "image/png");

        Assert.StartsWith(_factory.StorageServiceUrl + "/" + ChatApiFactory.StorageBucket + "/", ticket.Url);
        Assert.InRange(ticket.ExpiresAt, before.AddMinutes(10).AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(5));
    }

    // ##### Looking at an object #####

    [Fact]
    public async Task Head_reports_the_size_and_type_an_object_was_stored_with()
    {
        var key = NewKey();
        await _factory.PutObjectAsync(key, Bytes(1234), "application/pdf");

        var stored = await _storage.HeadAsync(key, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(1234, stored.SizeBytes);
        Assert.Equal("application/pdf", stored.ContentType);
        Assert.False(string.IsNullOrEmpty(stored.ETag));
    }

    [Fact]
    public async Task Head_returns_nothing_for_an_object_that_does_not_exist()
    {
        Assert.Null(await _storage.HeadAsync(NewKey(), CancellationToken.None));
    }

    // ##### Copying #####

    [Fact]
    public async Task Copy_duplicates_the_content_and_its_type_under_the_new_key()
    {
        var source = NewKey();
        var destination = NewKey("attachments");
        var content = Bytes(500);
        await _factory.PutObjectAsync(source, content, "image/webp");
        var stored = await _storage.HeadAsync(source, CancellationToken.None);

        await _storage.CopyAsync(source, destination, stored!.ETag, CancellationToken.None);

        Assert.Equal(content, await _factory.GetObjectBytesAsync(destination));
        Assert.Equal("image/webp", (await _storage.HeadAsync(destination, CancellationToken.None))!.ContentType);
        Assert.True(await _factory.ObjectExistsAsync(source));
    }

    // The gap between looking at an object and copying it: someone holding the upload link could put a bigger
    // file there in between. The copy only takes the content that was looked at.
    [Fact]
    public async Task Copy_refuses_an_object_that_was_replaced_after_it_was_looked_at()
    {
        var source = NewKey();
        var destination = NewKey("attachments");
        await _factory.PutObjectAsync(source, Bytes(100));
        var looked = await _storage.HeadAsync(source, CancellationToken.None);
        await _factory.PutObjectAsync(source, Bytes(200));

        await Assert.ThrowsAsync<BlobUnavailableException>(
            () => _storage.CopyAsync(source, destination, looked!.ETag, CancellationToken.None));

        Assert.False(await _factory.ObjectExistsAsync(destination));
    }

    [Fact]
    public async Task Copy_of_an_object_that_is_gone_says_so()
    {
        await Assert.ThrowsAsync<BlobUnavailableException>(
            () => _storage.CopyAsync(NewKey(), NewKey("attachments"), "\"anything\"", CancellationToken.None));
    }

    // ##### Deleting #####

    [Fact]
    public async Task Delete_removes_the_object_and_removing_a_missing_one_is_not_an_error()
    {
        var key = NewKey();
        await _factory.PutObjectAsync(key, Bytes(10));

        await _storage.DeleteAsync(key, CancellationToken.None);
        await _storage.DeleteAsync(key, CancellationToken.None);

        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    // ##### Downloading #####

    [Fact]
    public async Task A_download_link_returns_the_content()
    {
        var key = NewKey("attachments");
        var content = Bytes(777);
        await _factory.PutObjectAsync(key, content, "image/png");

        var response = await Http.GetAsync(_storage.CreateDownloadUrl(key, "photo.png", inline: true).Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_image_opens_in_the_page_and_any_other_file_downloads_under_its_own_name()
    {
        var key = NewKey("attachments");
        await _factory.PutObjectAsync(key, Bytes(50), "application/pdf");

        var inline = await Http.GetAsync(_storage.CreateDownloadUrl(key, "plan.pdf", inline: true).Url);
        var download = await Http.GetAsync(_storage.CreateDownloadUrl(key, "plan.pdf", inline: false).Url);

        Assert.Equal("inline", inline.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("plan.pdf", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    // The name comes from the sender. It must not be able to end the header and start another.
    [Fact]
    public async Task A_file_name_with_quotes_and_line_breaks_cannot_inject_a_header()
    {
        var key = NewKey("attachments");
        await _factory.PutObjectAsync(key, Bytes(50), "application/pdf");

        var response = await Http.GetAsync(
            _storage.CreateDownloadUrl(key, "a\"b\r\nX-Injected: 1.pdf", inline: false).Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Injected"));
        var disposition = string.Join(";", response.Content.Headers.GetValues("Content-Disposition"));
        Assert.DoesNotContain('\r', disposition);
        Assert.DoesNotContain('\n', disposition);
    }

    [Fact]
    public async Task A_non_ascii_file_name_survives_in_the_encoded_form()
    {
        var key = NewKey("attachments");
        await _factory.PutObjectAsync(key, Bytes(50), "application/pdf");

        var response = await Http.GetAsync(_storage.CreateDownloadUrl(key, "résumé 履歴書.pdf", inline: false).Url);

        Assert.Equal("résumé 履歴書.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
    }
}
