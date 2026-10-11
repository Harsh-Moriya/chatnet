using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChatNET.API.Tests.Infrastructure;

// Counts the SQL queries EF Core runs, so a test can assert that an endpoint uses a fixed
// number of queries however much data it returns. This is how an N+1 problem (one query for a
// list, then one more per row) is caught before it reaches production.
public sealed class QueryCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
