using Microsoft.Extensions.Options;

namespace ChatNET.API.Calls;

// Bound from the "Calls" configuration section and validated when the host starts. The limits of a call that is being made, ringing or
// running (the live state in Redis, see CallStore); the length of a ring is the one the product promises (30 seconds) and the rest are
// how quickly a call is noticed to be lost.
public sealed class CallOptions
{
    public const string SectionName = "Calls";

    // How long a call rings before it is missed. Enforced by the server, so a call whose caller closed the tab is still recorded.
    public TimeSpan RingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // How long a side whose hub connection dropped has to come back (RejoinCall) before the call ends. A network change drops the
    // connection as well as the media path, so a call that ended at once would not survive one.
    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(10);

    // How long a side counts as there without being renewed. The instance that holds a side's connection renews it; an instance that
    // dies stops renewing, and the call ends once the lease has run out.
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    // How often each instance looks for calls that are due (a ring that ran out, a lease or a grace that ended, an ending that was left).
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    // How often each instance renews the sides it holds. Well inside the lease, so one missed renewal does not end a call.
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);

    // How long after a call was moved to "ending" (its end is being recorded) another instance waits before it finishes the job, if the
    // first did not.
    public TimeSpan EndingRetry { get; set; } = TimeSpan.FromSeconds(5);

    // The largest signal (an SDP offer or answer, or an ICE candidate) in bytes of its text, and how many a call may carry.
    public int MaxSignalBytes { get; set; } = 16 * 1024;
    public int MaxSignalsPerCall { get; set; } = 600;
}

public sealed class CallOptionsValidator : IValidateOptions<CallOptions>
{
    public ValidateOptionsResult Validate(string? name, CallOptions options)
    {
        var failures = new List<string>();

        if (options.RingTimeout < TimeSpan.FromSeconds(1))
            failures.Add("Calls:RingTimeout must be at least one second.");

        if (options.ReconnectGrace < TimeSpan.FromSeconds(1))
            failures.Add("Calls:ReconnectGrace must be at least one second.");

        if (options.TickInterval <= TimeSpan.Zero)
            failures.Add("Calls:TickInterval must be greater than zero.");

        if (options.RenewInterval < options.TickInterval)
            failures.Add("Calls:RenewInterval must be at least Calls:TickInterval.");

        // A lease that one late renewal can outlast would end calls that are fine.
        if (options.Lease < options.RenewInterval * 2)
            failures.Add("Calls:Lease must be at least twice Calls:RenewInterval.");

        if (options.EndingRetry <= TimeSpan.Zero)
            failures.Add("Calls:EndingRetry must be greater than zero.");

        if (options.MaxSignalBytes < 1)
            failures.Add("Calls:MaxSignalBytes must be at least 1.");

        if (options.MaxSignalsPerCall < 1)
            failures.Add("Calls:MaxSignalsPerCall must be at least 1.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
