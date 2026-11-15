using ChatNET.API.Calls.Ice;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Calls;

// The settings of the relay are validated when the host starts: a secret without an address, or an address that is not one, would
// otherwise show up as the first call that needs the relay failing. Without a secret the relay is off, which is a valid state, whatever
// the addresses are (the development settings carry them).
public class TurnOptionsTests
{
    private static readonly TurnOptionsValidator Validator = new();

    private static TurnOptions Valid() => new()
    {
        Secret = new string('s', TurnOptionsValidator.MinSecretLength),
        Urls = ["turn:chat.example.com:3478?transport=udp"],
        StunUrls = ["stun:chat.example.com:3478"],
    };

    private static ValidateOptionsResult Check(TurnOptions options) => Validator.Validate(null, options);

    [Fact]
    public void A_secret_with_addresses_is_valid_and_turns_the_relay_on()
    {
        var options = Valid();

        Assert.True(Check(options).Succeeded);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void Nothing_set_is_valid_and_the_relay_is_off()
    {
        var options = new TurnOptions();

        Assert.True(Check(options).Succeeded);
        Assert.False(options.Enabled);
    }

    [Fact]
    public void Addresses_without_a_secret_are_valid_and_the_relay_is_off()
    {
        var options = Valid();
        options.Secret = string.Empty;

        Assert.True(Check(options).Succeeded);
        Assert.False(options.Enabled);
    }

    [Fact]
    public void A_secret_without_a_relay_address_is_refused()
    {
        var options = Valid();
        options.Urls = [];

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("Turn:Urls must name at least one relay", result.FailureMessage);
    }

    [Fact]
    public void A_secret_that_is_too_short_is_refused_without_repeating_it()
    {
        var options = Valid();
        options.Secret = "short-and-guessable";

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("at least 32 characters", result.FailureMessage);
        Assert.DoesNotContain("short-and-guessable", result.FailureMessage);
    }

    [Theory]
    [InlineData("turn:chat.example.com")]
    [InlineData("turn:chat.example.com:3478")]
    [InlineData("turns:chat.example.com:5349?transport=tcp")]
    [InlineData("turn:203.0.113.10:3478?transport=udp")]
    [InlineData("turn:[2001:db8::1]:3478")]
    public void Relay_addresses_in_the_forms_a_browser_takes_are_valid(string url)
    {
        var options = Valid();
        options.Urls = [url];

        Assert.True(Check(options).Succeeded);
    }

    [Theory]
    [InlineData("chat.example.com:3478")]
    [InlineData("stun:chat.example.com:3478")]
    [InlineData("https://chat.example.com")]
    [InlineData("turn:")]
    [InlineData("turn:chat example.com")]
    [InlineData("turn:chat.example.com:3478?transport=sctp")]
    [InlineData("turn:chat.example.com:3478?transport=udp&x=1")]
    public void An_address_that_is_not_a_turn_address_is_refused(string url)
    {
        var options = Valid();
        options.Urls = [url];

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("Turn:Urls has an entry that is not a turn: or turns: address", result.FailureMessage);
    }

    [Theory]
    [InlineData("turn:chat.example.com:3478")]
    [InlineData("chat.example.com:3478")]
    [InlineData("stun:chat.example.com:3478?transport=udp")]
    public void A_stun_address_has_to_be_a_stun_address(string url)
    {
        var options = Valid();
        options.StunUrls = [url];

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("Turn:StunUrls has an entry that is not a stun: or stuns: address", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(24 * 3600 + 1)]
    public void A_lifetime_outside_one_minute_to_a_day_is_refused(int seconds)
    {
        var options = Valid();
        options.CredentialLifetime = TimeSpan.FromSeconds(seconds);

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("Turn:CredentialLifetime", result.FailureMessage);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(3600)]
    [InlineData(24 * 3600)]
    public void A_lifetime_inside_it_is_valid_at_both_ends(int seconds)
    {
        var options = Valid();
        options.CredentialLifetime = TimeSpan.FromSeconds(seconds);

        Assert.True(Check(options).Succeeded);
    }
}
