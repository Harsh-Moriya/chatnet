using System.Text.Json;
using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Tests.Infrastructure;

// Asks Postgres how it actually executed a query: EXPLAIN (ANALYZE) runs the query and reports,
// for every table access, how many rows it really read. Counting rows instead of measuring time
// makes a performance test deterministic: the number depends on the query's shape, not on how
// busy the machine is.
public static class QueryPlan
{
    public static async Task<long> RowsReadFromTableAsync(AppDbContext db, CommandRecorder.Recorded query, string table)
    {
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + query.Sql, connection);
        foreach (var (name, value) in query.Parameters)
            explain.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));

        var json = Convert.ToString(await explain.ExecuteScalarAsync())!;
        using var plan = JsonDocument.Parse(json);

        return RowsRead(plan.RootElement[0].GetProperty("Plan"), table);
    }

    // Walks the plan tree. A node reports its per-loop average, so it is multiplied by the number
    // of loops (a lateral join runs its inner lookup once per outer row). Rows the node read and
    // then discarded with a filter count too: they were read all the same.
    private static long RowsRead(JsonElement node, string table)
    {
        long total = 0;

        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == table)
        {
            var loops = node.GetProperty("Actual Loops").GetDouble();
            total += (long)(node.GetProperty("Actual Rows").GetDouble() * loops);

            if (node.TryGetProperty("Rows Removed by Filter", out var removed))
                total += (long)(removed.GetDouble() * loops);
        }

        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                total += RowsRead(child, table);

        return total;
    }
}
