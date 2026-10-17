using ChatNET.API.Presence;

namespace ChatNET.API.Tests.Presence;

// The presence settings are checked when the application starts, so a mistake stops it with a message
// naming the setting instead of showing up later as people flickering offline.
public class PresenceOptionsTests
{
    private static string[] Failures(PresenceOptions options) =>
        new PresenceOptionsValidator().Validate(null, options).Failures?.ToArray() ?? [];

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.Empty(Failures(new PresenceOptions()));
    }

    [Fact]
    public void A_refresh_interval_of_zero_or_less_is_refused()
    {
        var failures = Failures(new PresenceOptions { RefreshInterval = TimeSpan.Zero });

        Assert.Contains(failures, f => f.Contains("Presence:RefreshInterval"));
    }

    // One refresh that is late or lost must not take anybody offline.
    [Fact]
    public void An_expiry_shorter_than_two_refresh_intervals_is_refused()
    {
        var failures = Failures(new PresenceOptions
        {
            RefreshInterval = TimeSpan.FromSeconds(30),
            Ttl = TimeSpan.FromSeconds(59),
        });

        Assert.Contains(failures, f => f.Contains("Presence:Ttl"));
    }

    [Fact]
    public void An_expiry_of_exactly_two_refresh_intervals_is_accepted()
    {
        Assert.Empty(Failures(new PresenceOptions
        {
            RefreshInterval = TimeSpan.FromSeconds(30),
            Ttl = TimeSpan.FromSeconds(60),
        }));
    }

    [Fact]
    public void A_negative_grace_period_is_refused()
    {
        var failures = Failures(new PresenceOptions { OfflineGrace = TimeSpan.FromSeconds(-1) });

        Assert.Contains(failures, f => f.Contains("Presence:OfflineGrace"));
    }

    [Fact]
    public void A_grace_period_longer_than_the_expiry_is_refused()
    {
        var failures = Failures(new PresenceOptions
        {
            Ttl = TimeSpan.FromSeconds(90),
            OfflineGrace = TimeSpan.FromSeconds(91),
        });

        Assert.Contains(failures, f => f.Contains("Presence:OfflineGrace"));
    }

    [Fact]
    public void No_grace_period_at_all_is_accepted()
    {
        Assert.Empty(Failures(new PresenceOptions { OfflineGrace = TimeSpan.Zero }));
    }

    [Fact]
    public void Every_mistake_is_reported_at_once()
    {
        var failures = Failures(new PresenceOptions
        {
            RefreshInterval = TimeSpan.Zero,
            Ttl = TimeSpan.Zero,
            OfflineGrace = TimeSpan.FromSeconds(-1),
        });

        Assert.Equal(2, failures.Length);
    }
}
