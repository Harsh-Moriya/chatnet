using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChatNET.API.Common.Health;

// Healthy when the application can open a connection to Postgres. CanConnectAsync reports a failure
// as false instead of throwing, and the registration's timeout cuts off a database that accepts the
// connection but never answers.
public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;

    public PostgresHealthCheck(AppDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return await _db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Postgres did not accept a connection.");
    }
}
