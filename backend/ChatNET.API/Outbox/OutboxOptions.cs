using Microsoft.Extensions.Options;

namespace ChatNET.API.Outbox;

// Bound from the "Outbox" configuration section and validated when the host starts.
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    // How often each server looks for pushes that were not made.
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(2);

    // How long a new row is left to the request that wrote it before the relay may take it. Long enough for a normal
    // request to push and mark the row; short enough that a failed push is not noticed late.
    public TimeSpan ClaimAfter { get; set; } = TimeSpan.FromSeconds(5);

    // The wait before the relay tries a row again after it took it. It doubles with every attempt, up to 64 times
    // this, so a long outage is not met with a retry every few seconds for every row.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    // How many rows one pass takes.
    public int BatchSize { get; set; } = 100;

    // After this many attempts the row is left alone and stays in the table (with its last error) until it is
    // removed with the finished ones.
    public int MaxAttempts { get; set; } = 10;

    // How long a finished row (pushed, or given up on) stays before it is removed.
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(1);

    // How often finished rows are looked for.
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

public sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        var failures = new List<string>();

        if (options.RelayInterval <= TimeSpan.Zero)
            failures.Add("Outbox:RelayInterval must be greater than zero.");

        if (options.ClaimAfter < TimeSpan.Zero)
            failures.Add("Outbox:ClaimAfter must not be negative.");

        if (options.RetryDelay <= TimeSpan.Zero)
            failures.Add("Outbox:RetryDelay must be greater than zero.");

        if (options.BatchSize < 1)
            failures.Add("Outbox:BatchSize must be at least 1.");

        if (options.MaxAttempts < 1)
            failures.Add("Outbox:MaxAttempts must be at least 1.");

        if (options.Retention <= TimeSpan.Zero)
            failures.Add("Outbox:Retention must be greater than zero.");

        if (options.CleanupInterval <= TimeSpan.Zero)
            failures.Add("Outbox:CleanupInterval must be greater than zero.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
