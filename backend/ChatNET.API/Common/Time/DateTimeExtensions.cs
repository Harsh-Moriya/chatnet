namespace ChatNET.API.Common.Time;

public static class DateTimeExtensions
{
    // .NET keeps time to 100 nanoseconds, Postgres to 1 microsecond. A timestamp that is returned to
    // a client before it is read back from the database would otherwise differ from the stored one
    // in the last digit, so the same message would carry two slightly different times.
    public static DateTime TruncateToMicroseconds(this DateTime value)
        => new(value.Ticks - value.Ticks % 10, value.Kind);
}
