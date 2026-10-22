using ChatNET.API.Media;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Media;

// The storage settings are checked when the application starts, so a mistake stops it with a message naming
// the setting instead of showing up when the first person attaches a file.
[Collection(IntegrationTestCollection.Name)]
public class StorageOptionsTests
{
    private readonly ChatApiFactory _factory;

    public StorageOptionsTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static StorageOptions Valid() => new()
    {
        ServiceUrl = "http://localhost:9000",
        Region = "auto",
        Bucket = "chatnet",
        AccessKey = "an-access-key",
        SecretKey = "a-secret-key",
        MaxFileBytes = 10 * 1024 * 1024,
        MaxFilesPerMessage = 4,
        AllowedContentTypes = ["image/png", "application/pdf"],
        UploadUrlMinutes = 10,
        DownloadUrlMinutes = 60,
    };

    private static string[] Failures(StorageOptions options) =>
        new StorageOptionsValidator().Validate(null, options).Failures?.ToArray() ?? [];

    // ##### The validator #####

    [Fact]
    public void A_complete_set_of_options_is_valid()
    {
        Assert.Empty(Failures(Valid()));
    }

    [Fact]
    public void Nothing_is_valid_by_default_because_the_bucket_and_keys_belong_to_the_deployment()
    {
        var failures = Failures(new StorageOptions());

        Assert.Contains(failures, f => f.Contains("Storage:ServiceUrl"));
        Assert.Contains(failures, f => f.Contains("Storage:Bucket"));
        Assert.Contains(failures, f => f.Contains("Storage:AccessKey"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:9000")]
    [InlineData("ftp://example.test")]
    [InlineData("/relative/path")]
    public void A_service_url_that_is_not_an_absolute_http_address_is_refused(string url)
    {
        var options = Valid();
        options.ServiceUrl = url;

        Assert.Contains(Failures(options), f => f.Contains("Storage:ServiceUrl"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Has-Capitals")]
    [InlineData("under_score")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    public void A_bucket_name_that_breaks_the_naming_rules_is_refused(string bucket)
    {
        var options = Valid();
        options.Bucket = bucket;

        Assert.Contains(Failures(options), f => f.Contains("Storage:Bucket"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("chatnet-uploads-2")]
    public void A_bucket_name_that_follows_the_naming_rules_is_accepted(string bucket)
    {
        var options = Valid();
        options.Bucket = bucket;

        Assert.Empty(Failures(options));
    }

    [Fact]
    public void A_missing_key_or_secret_is_refused_without_repeating_the_other_one()
    {
        var options = Valid();
        options.SecretKey = "";

        var failure = Assert.Single(Failures(options));

        Assert.Contains("Storage:AccessKey", failure);
        Assert.DoesNotContain(options.AccessKey, failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_file_size_limit_of_zero_or_less_is_refused(long bytes)
    {
        var options = Valid();
        options.MaxFileBytes = bytes;

        Assert.Contains(Failures(options), f => f.Contains("Storage:MaxFileBytes"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void A_files_per_message_limit_outside_one_to_twenty_is_refused(int count)
    {
        var options = Valid();
        options.MaxFilesPerMessage = count;

        Assert.Contains(Failures(options), f => f.Contains("Storage:MaxFilesPerMessage"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("png")]
    [InlineData(" ")]
    public void A_content_type_that_is_not_type_slash_subtype_is_refused(string type)
    {
        var options = Valid();
        options.AllowedContentTypes = ["image/png", type];

        Assert.Contains(Failures(options), f => f.Contains("Storage:AllowedContentTypes"));
    }

    [Fact]
    public void An_empty_list_of_content_types_is_refused()
    {
        var options = Valid();
        options.AllowedContentTypes = [];

        Assert.Contains(Failures(options), f => f.Contains("Storage:AllowedContentTypes"));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(10, 0)]
    public void A_link_lifetime_of_zero_is_refused(int upload, int download)
    {
        var options = Valid();
        options.UploadUrlMinutes = upload;
        options.DownloadUrlMinutes = download;

        Assert.Contains(Failures(options), f => f.Contains("UrlMinutes"));
    }

    // ##### Start-up #####

    [Theory]
    [InlineData("Storage:ServiceUrl", "not a url", "Storage:ServiceUrl")]
    [InlineData("Storage:Bucket", "", "Storage:Bucket")]
    [InlineData("Storage:AccessKey", "", "Storage:AccessKey")]
    [InlineData("Storage:SecretKey", "", "Storage:SecretKey")]
    [InlineData("Storage:MaxFileBytes", "0", "Storage:MaxFileBytes")]
    public void Startup_fails_when_a_storage_setting_is_not_valid(string setting, string value, string named)
    {
        using var host = WithSetting(setting, value);

        Assert.Contains(named, StartupFailureMessage(host));
    }

    [Fact]
    public void Startup_failure_for_the_credentials_says_how_to_set_them_and_never_prints_them()
    {
        using var host = WithSetting("Storage:AccessKey", "");

        var message = StartupFailureMessage(host);

        Assert.Contains("user-secrets", message);
        Assert.DoesNotContain(_factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value.SecretKey, message);
    }

    private WebApplicationFactory<Program> WithSetting(string key, string value) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

    // The host is built lazily, so the failure appears when the first client is created. Every message in the
    // chain of inner exceptions is joined so the test does not depend on how many wrappers the test host adds.
    private static string StartupFailureMessage(WebApplicationFactory<Program> factory)
    {
        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);

        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
            messages.Add(current.Message);

        return string.Join(" | ", messages);
    }
}
