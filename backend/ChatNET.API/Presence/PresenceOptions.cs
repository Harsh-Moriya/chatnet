namespace ChatNET.API.Presence;

// Bound from the "Presence" configuration section and validated when the host starts.
public sealed class PresenceOptions
{
    public const string SectionName = "Presence";

    // How often each server refreshes the entries of the connections it holds, and how often it
    // looks for entries that were left behind.
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    // How long an entry counts as alive without being refreshed. A server that stops (crash,
    // network cut) leaves its entries behind; they stop counting after this long.
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(90);

    // How long to wait after someone's last connection closed before telling their contacts they are
    // offline. A page refresh closes one connection and opens another a moment later; without the wait
    // every refresh would flash "offline" at everyone who can see them.
    public TimeSpan OfflineGrace { get; set; } = TimeSpan.FromSeconds(5);
}
