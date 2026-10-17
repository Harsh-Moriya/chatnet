using Microsoft.Extensions.Options;

namespace ChatNET.API.Presence;

public sealed class PresenceOptionsValidator : IValidateOptions<PresenceOptions>
{
    public ValidateOptionsResult Validate(string? name, PresenceOptions options)
    {
        var failures = new List<string>();

        if (options.RefreshInterval <= TimeSpan.Zero)
            failures.Add("Presence:RefreshInterval must be greater than zero.");

        // One refresh that is late or lost must not take anybody offline, so an entry has to survive
        // at least two refresh intervals.
        if (options.Ttl < options.RefreshInterval * 2)
            failures.Add("Presence:Ttl must be at least twice Presence:RefreshInterval, so one missed refresh does not take anyone offline.");

        if (options.OfflineGrace < TimeSpan.Zero)
            failures.Add("Presence:OfflineGrace must not be negative.");

        if (options.OfflineGrace > options.Ttl)
            failures.Add("Presence:OfflineGrace must not be longer than Presence:Ttl.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
