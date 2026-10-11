using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChatNET.API.Tests.Infrastructure;

// Remembers the SQL text and parameter values of every query EF Core runs, so a test can take
// the query an endpoint really executed and ask Postgres how it would run it (see QueryPlan).
public sealed class CommandRecorder : DbCommandInterceptor
{
    public sealed record Recorded(string Sql, IReadOnlyDictionary<string, object?> Parameters);

    private readonly ConcurrentQueue<Recorded> _commands = new();

    public IReadOnlyList<Recorded> Commands => _commands.ToArray();

    public void Clear()
    {
        while (_commands.TryDequeue(out _))
        {
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    // The command object is reused by EF, so its text and parameters are copied immediately.
    private void Record(DbCommand command)
    {
        var parameters = new Dictionary<string, object?>();
        foreach (DbParameter parameter in command.Parameters)
            parameters[parameter.ParameterName] = parameter.Value == DBNull.Value ? null : parameter.Value;

        _commands.Enqueue(new Recorded(command.CommandText, parameters));
    }
}
