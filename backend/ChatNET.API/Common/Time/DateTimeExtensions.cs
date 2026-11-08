namespace ChatNET.API.Common.Time;

public static class DateTimeExtensions
{
    // .NET keeps time to 100 nanoseconds, Postgres to 1 microsecond. A timestamp that is returned to
    // a client before it is read back from the database would otherwise differ from the stored one
    // in the last digit, so the same message would carry two slightly different times.
    public static DateTime TruncateToMicroseconds(this DateTime value)
        => new(value.Ticks - value.Ticks % 10, value.Kind);

    // Whole seconds, for a moment that is part of a ticket a client acts on: JavaScript only promises to parse three fractional
    // digits, and a client that gets NaN for an expiry would treat the ticket as expired at once.
    public static DateTime TruncateToSeconds(this DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}
