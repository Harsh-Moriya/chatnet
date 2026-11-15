using System.Text.Json;
using System.Text.Json.Serialization;
using ChatNET.API.Calls.Ice;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Calls;

// The servers a call is given: a STUN entry, and a TURN entry with a credential made for the person under the secret the relay shares. The
// credential is what the relay checks, so it is pinned against a value made by another implementation (openssl), and its time is the one
// the relay refuses it after.
public class IceServersTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private static readonly Guid Ana = Guid.Parse("01a1ddf1-d39e-7903-a7f1-42c7632aa24b");

    // One hour before 1800000000, so a credential of an hour ends exactly there.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_799_996_400);

    private static IceServers Servers(TurnOptions options, ManualTimeProvider? clock = null) =>
        new(Options.Create(options), clock ?? new ManualTimeProvider(Now));

    private static TurnOptions On() => new()
    {
        Secret = Secret,
        Urls = ["turn:chat.example.com:3478?transport=udp", "turn:chat.example.com:3478?transport=tcp"],
        StunUrls = ["stun:chat.example.com:3478"],
    };

    [Fact]
    public void The_credential_is_the_HMAC_SHA1_of_the_username_under_the_secret_as_another_implementation_makes_it()
    {
        // printf '%s' '1800000000:01a1ddf1-d39e-7903-a7f1-42c7632aa24b' | openssl dgst -sha1 -hmac 0123456789abcdef0123456789abcdef -binary | openssl base64
        var turn = Servers(On()).For(Ana).Last();

        Assert.Equal("1800000000:01a1ddf1-d39e-7903-a7f1-42c7632aa24b", turn.Username);
        Assert.Equal("ptZFZxJlmRNmvegaufBR5FqZIqQ=", turn.Credential);
    }

    [Fact]
    public void The_username_ends_where_the_lifetime_does_and_the_lifetime_is_the_one_set()
    {
        var options = On();
        options.CredentialLifetime = TimeSpan.FromMinutes(10);

        var turn = Servers(options).For(Ana).Last();

        Assert.Equal($"{Now.AddMinutes(10).ToUnixTimeSeconds()}:{Ana}", turn.Username);
    }

    [Fact]
    public void A_credential_made_later_ends_later()
    {
        var clock = new ManualTimeProvider(Now);
        var servers = Servers(On(), clock);
        var first = servers.For(Ana).Last();

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = servers.For(Ana).Last();

        Assert.NotEqual(first.Username, second.Username);
        Assert.NotEqual(first.Credential, second.Credential);
        Assert.StartsWith((Now.AddHours(1).ToUnixTimeSeconds() + 300).ToString(), second.Username);
    }

    [Fact]
    public void The_username_names_the_person_by_id_and_says_nothing_of_the_secret()
    {
        var turn = Servers(On()).For(Ana).Last();

        Assert.Contains(Ana.ToString(), turn.Username);
        Assert.DoesNotContain(Secret, turn.Username);
        Assert.DoesNotContain(Secret, turn.Credential);
    }

    [Fact]
    public void Two_people_get_different_credentials_and_one_secret_gives_another_credential_than_a_second_secret()
    {
        var other = On();
        other.Secret = "fedcba9876543210fedcba9876543210";

        var ana = Servers(On()).For(Ana).Last();
        var ben = Servers(On()).For(Guid.NewGuid()).Last();
        var anaUnderOther = Servers(other).For(Ana).Last();

        Assert.NotEqual(ana.Credential, ben.Credential);
        Assert.Equal(ana.Username, anaUnderOther.Username);
        Assert.NotEqual(ana.Credential, anaUnderOther.Credential);
    }

    [Fact]
    public void The_stun_entry_comes_first_and_has_no_credentials_and_the_turn_entry_carries_every_relay_address()
    {
        var servers = Servers(On()).For(Ana);

        Assert.Equal(2, servers.Count);
        Assert.Equal(["stun:chat.example.com:3478"], servers[0].Urls);
        Assert.Null(servers[0].Username);
        Assert.Null(servers[0].Credential);
        Assert.Equal(["turn:chat.example.com:3478?transport=udp", "turn:chat.example.com:3478?transport=tcp"], servers[1].Urls);
    }

    [Fact]
    public void Without_stun_addresses_there_is_only_the_relay()
    {
        var options = On();
        options.StunUrls = [];

        Assert.Single(Servers(options).For(Ana));
    }

    [Fact]
    public void Without_a_secret_nothing_is_handed_out_whatever_the_addresses_are()
    {
        var options = On();
        options.Secret = string.Empty;

        Assert.Empty(Servers(options).For(Ana));
    }

    [Fact]
    public void The_json_leaves_out_what_is_not_there_so_a_browser_never_reads_null_as_a_name()
    {
        var servers = Servers(On()).For(Ana);
        var json = JsonSerializer.Serialize(servers, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });

        var stun = JsonDocument.Parse(json).RootElement[0];
        var turn = JsonDocument.Parse(json).RootElement[1];

        Assert.False(stun.TryGetProperty("username", out _));
        Assert.False(stun.TryGetProperty("credential", out _));
        Assert.True(turn.TryGetProperty("username", out _));
        Assert.True(turn.TryGetProperty("credential", out _));
        Assert.True(turn.TryGetProperty("urls", out _));
    }
}
