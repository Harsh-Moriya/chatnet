namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// Too many failed attempts; the caller must wait before trying again. Maps to HTTP 429 and
/// sets a Retry-After header.
/// </summary>
public sealed class TooManyAttemptsException : ChatNETException
{
    public TimeSpan RetryAfter { get; }

    public TooManyAttemptsException(TimeSpan retryAfter)
        : base(BuildMessage(retryAfter))
    {
        RetryAfter = retryAfter;
    }

    private static string BuildMessage(TimeSpan retryAfter)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
        var unit = minutes == 1 ? "minute" : "minutes";
        return $"Too many failed sign-in attempts. Try again in {minutes} {unit}.";
    }
}
