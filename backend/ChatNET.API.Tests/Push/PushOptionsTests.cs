using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Push;

// The settings of push are validated when the host starts: a half-set or malformed key would otherwise show up as the first
// push failing, long after the deployment. With none of the three set the feature is off, which is a valid state.
public class PushOptionsTests
{
    private static readonly PushOptionsValidator Validator = new();

    private static PushOptions Valid() => new()
    {
        PublicKey = PushApi.Vapid.PublicKey,
        PrivateKey = PushApi.Vapid.PrivateKey,
        Subject = PushApi.Vapid.Subject,
        AllowedHosts = ["fcm.googleapis.com"],
    };

    private static ValidateOptionsResult Check(PushOptions options) => Validator.Validate(null, options);

    [Fact]
    public void Keys_that_are_set_properly_are_valid_and_turn_push_on()
    {
        var options = Valid();

        Assert.True(Check(options).Succeeded);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void With_none_of_the_three_set_push_is_off_and_that_is_valid()
    {
        var options = new PushOptions();

        Assert.True(Check(options).Succeeded);
        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("PublicKey")]
    [InlineData("PrivateKey")]
    [InlineData("Subject")]
    public void Setting_some_of_the_three_but_not_all_is_refused(string missing)
    {
        var options = Valid();
        switch (missing)
        {
            case "PublicKey": options.PublicKey = string.Empty; break;
            case "PrivateKey": options.PrivateKey = string.Empty; break;
            default: options.Subject = string.Empty; break;
        }

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("must all be set, or none of them", result.FailureMessage);
    }

    [Theory]
    [InlineData("Push:PublicKey", "not-a-key")]
    [InlineData("Push:PublicKey", "AAAA")]
    [InlineData("Push:PrivateKey", "not-a-key")]
    [InlineData("Push:PrivateKey", "AAAA")]
    [InlineData("Push:Subject", "push@example.test")]
    [InlineData("Push:Subject", "http://example.test")]
    public void A_key_or_subject_that_is_malformed_is_named_in_the_failure(string setting, string value)
    {
        var options = Valid();
        switch (setting)
        {
            case "Push:PublicKey": options.PublicKey = value; break;
            case "Push:PrivateKey": options.PrivateKey = value; break;
            default: options.Subject = value; break;
        }

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains(setting, result.FailureMessage);
    }

    // Two of the three are secrets, and a failure is logged when the host does not start.
    [Fact]
    public void A_failure_never_repeats_the_value_that_was_wrong()
    {
        var options = Valid();
        options.PrivateKey = "this-private-key-is-wrong";

        Assert.DoesNotContain("this-private-key-is-wrong", Check(options).FailureMessage);
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com")]
    [InlineData("fcm.googleapis.com/path")]
    [InlineData("FCM.googleapis.com")]
    [InlineData("")]
    [InlineData("two words")]
    public void An_allowed_host_has_to_be_a_plain_lower_case_host_name(string host)
    {
        var options = Valid();
        options.AllowedHosts = [host];

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("Push:AllowedHosts", result.FailureMessage);
    }

    [Fact]
    public void With_push_on_at_least_one_host_has_to_be_allowed()
    {
        var options = Valid();
        options.AllowedHosts = [];

        Assert.Contains("Push:AllowedHosts", Check(options).FailureMessage);
    }

    [Fact]
    public void Room_for_no_browsers_at_all_is_refused()
    {
        var options = Valid();
        options.MaxSubscriptionsPerUser = 0;

        Assert.Contains("Push:MaxSubscriptionsPerUser", Check(options).FailureMessage);
    }

    // The push services a deployment starts with: Chrome and the browsers built on it, Firefox, Safari, and Edge on Windows. Read
    // from the file itself, because the test host does not override it.
    [Fact]
    public void The_settings_file_allows_the_push_services_of_the_major_browsers()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();

        var options = settings.GetSection(PushOptions.SectionName).Get<PushOptions>()!;

        Assert.Equal(
            ["fcm.googleapis.com", "updates.push.services.mozilla.com", "push.apple.com", "notify.windows.com"],
            options.AllowedHosts);
        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("Push:TimeToLive")]
    [InlineData("Push:RequestTimeout")]
    public void A_duration_that_is_zero_is_refused(string setting)
    {
        var options = Valid();
        if (setting == "Push:TimeToLive")
            options.TimeToLive = TimeSpan.Zero;
        else
            options.RequestTimeout = TimeSpan.Zero;

        Assert.Contains(setting, Check(options).FailureMessage);
    }

    [Fact]
    public void Sending_to_no_browsers_at_a_time_is_refused()
    {
        var options = Valid();
        options.MaxParallelSends = 0;

        Assert.Contains("Push:MaxParallelSends", Check(options).FailureMessage);
    }

    // The header that carries it counts whole seconds, so less than one is not a time to live.
    [Fact]
    public void A_time_to_live_of_less_than_a_second_is_refused()
    {
        var options = Valid();
        options.TimeToLive = TimeSpan.FromMilliseconds(500);

        Assert.Contains("Push:TimeToLive", Check(options).FailureMessage);
    }

    // The defaults are what was agreed: ten browsers a person, a push held for a day, ten seconds for a push service to answer, eight
    // pushes at a time.
    [Fact]
    public void The_defaults_are_the_agreed_ones()
    {
        var options = new PushOptions();

        Assert.Equal(10, options.MaxSubscriptionsPerUser);
        Assert.Equal(TimeSpan.FromDays(1), options.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RequestTimeout);
        Assert.Equal(8, options.MaxParallelSends);
    }
}
