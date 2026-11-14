using ChatNET.API.Common.Hosting;
using ChatNET.API.Keys;
using System.Buffers.Text;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Keys;

// The settings of encryption are validated when the host starts, so a wrong one is found at the start and not by the first person
// who links a device.
[Collection(IntegrationTestCollection.Name)]
public class EncryptionOptionsTests
{
    private readonly ChatApiFactory _factory;

    public EncryptionOptionsTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static ValidateOptionsResult Check(EncryptionOptions options) => new EncryptionOptionsValidator().Validate(null, options);

    [Fact]
    public void The_defaults_are_a_ten_minute_link_ten_devices_five_open_requests_and_a_64_kilobyte_backup()
    {
        var options = new EncryptionOptions();

        Assert.Equal(TimeSpan.FromMinutes(10), options.LinkLifetime);
        Assert.Equal(10, options.MaxDevicesPerUser);
        Assert.Equal(5, options.MaxPendingLinkRequests);
        Assert.Equal(64 * 1024, options.MaxBackupBytes);
        Assert.True(Check(options).Succeeded);
    }

    [Fact]
    public void The_bounds_themselves_are_accepted()
    {
        Assert.True(Check(new EncryptionOptions { LinkLifetime = TimeSpan.FromMinutes(1) }).Succeeded);
        Assert.True(Check(new EncryptionOptions { LinkLifetime = TimeSpan.FromHours(1) }).Succeeded);
        Assert.True(Check(new EncryptionOptions { MaxDevicesPerUser = 1 }).Succeeded);
        Assert.True(Check(new EncryptionOptions { MaxDevicesPerUser = 50 }).Succeeded);
        Assert.True(Check(new EncryptionOptions { MaxPendingLinkRequests = 1 }).Succeeded);
        Assert.True(Check(new EncryptionOptions { MaxPendingLinkRequests = 20 }).Succeeded);
        Assert.True(Check(new EncryptionOptions { MaxBackupBytes = 1024 }).Succeeded);
        Assert.True(Check(new EncryptionOptions { MaxBackupBytes = RequestLimits.LargestBackupBytes }).Succeeded);
    }

    [Fact]
    public void Each_setting_out_of_range_is_refused_by_name()
    {
        var cases = new (EncryptionOptions Options, string Key)[]
        {
            (new EncryptionOptions { LinkLifetime = TimeSpan.FromSeconds(59) }, "Encryption:LinkLifetime"),
            (new EncryptionOptions { LinkLifetime = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1) }, "Encryption:LinkLifetime"),
            (new EncryptionOptions { MaxDevicesPerUser = 0 }, "Encryption:MaxDevicesPerUser"),
            (new EncryptionOptions { MaxDevicesPerUser = 51 }, "Encryption:MaxDevicesPerUser"),
            (new EncryptionOptions { MaxPendingLinkRequests = 0 }, "Encryption:MaxPendingLinkRequests"),
            (new EncryptionOptions { MaxPendingLinkRequests = 21 }, "Encryption:MaxPendingLinkRequests"),
            (new EncryptionOptions { MaxBackupBytes = 1023 }, "Encryption:MaxBackupBytes"),
            (new EncryptionOptions { MaxBackupBytes = RequestLimits.LargestBackupBytes + 1 }, "Encryption:MaxBackupBytes"),
        };

        foreach (var (options, key) in cases)
        {
            var result = Check(options);
            Assert.True(result.Failed, key);
            Assert.Contains(key, result.FailureMessage);
        }
    }

    // A backup travels as base64url in a JSON body, next to a bundle and a password in the same request, and the host refuses a body over its
    // limit before any rule runs. A setting the validator accepts must therefore be a backup that can still be sent.
    [Fact]
    public void The_largest_backup_the_settings_allow_fits_in_a_request_body_once_it_is_encoded()
    {
        var limit = _factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize!.Value;

        var largest = 1024;
        while (Check(new EncryptionOptions { MaxBackupBytes = largest + 1 }).Succeeded) largest++;

        const int everythingElseInTheBody = 4096;
        Assert.True(Base64Url.GetEncodedLength(largest) + everythingElseInTheBody <= limit, $"A backup of {largest} bytes is {Base64Url.GetEncodedLength(largest)} characters.");
    }

    [Fact]
    public void Several_mistakes_are_all_reported()
    {
        var result = Check(new EncryptionOptions { MaxDevicesPerUser = 0, MaxBackupBytes = 1 });

        Assert.Contains("Encryption:MaxDevicesPerUser", result.FailureMessage);
        Assert.Contains("Encryption:MaxBackupBytes", result.FailureMessage);
    }

    [Fact]
    public void The_host_does_not_start_with_a_setting_out_of_range()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:MaxDevicesPerUser"] = "0" })));

        var failure = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("Encryption:MaxDevicesPerUser", failure.ToString());
    }
}
