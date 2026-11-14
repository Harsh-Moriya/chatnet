using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChatNET.API.Tests.Common;

// A rejected row makes Postgres say which row it was ("Failing row contains (...)"), and the driver keeps that out of the exception unless
// the connection string asks for it. The exception is what the logs write when a request fails with an error nobody handled, and the rows
// that can be rejected hold sealed messages and sealed keys, so the logs would carry them. The values below stand for such a row.
[Collection(IntegrationTestCollection.Name)]
public class DatabaseErrorDetailTests
{
    private readonly ChatApiFactory _factory;

    public DatabaseErrorDetailTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_rejected_row_is_not_part_of_the_exception_a_failed_request_logs()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // One statement, so the table and the insert share a connection. The check constraint refuses a value of any length but one, and the
        // value is made inside the statement so that it is not in the text of it either, which a database error also reports.
        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "DO $$ BEGIN CREATE TEMP TABLE sealed_probe (v text CONSTRAINT ck_sealed_probe CHECK (length(v) = 1)); INSERT INTO sealed_probe SELECT repeat('x', 30); END $$;"));

        Assert.Equal("23514", failure.SqlState);
        Assert.StartsWith("Detail redacted", failure.Detail);
        Assert.DoesNotContain(new string('x', 30), failure.ToString());
    }
}
