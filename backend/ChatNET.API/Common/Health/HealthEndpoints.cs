using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChatNET.API.Common.Health;

public record HealthResponse(string Status, Dictionary<string, string> Checks);

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // 200 when the instance can do its job, 503 when a dependency it needs is down, so a load
        // balancer takes it out of rotation. The body names each dependency and its state and never
        // says why a check failed: this endpoint is public, and a connection error can contain host
        // names or credentials. The reason is in the log.
        app.MapGet("/health", async (HealthCheckService health, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            var logger = loggers.CreateLogger("ChatNET.API.Health");

            var report = await health.CheckHealthAsync(cancellationToken);

            foreach (var (name, entry) in report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy))
                logger.LogWarning(entry.Exception, "Health check {Check} is {Status}: {Description}", name, entry.Status, entry.Description);

            var up = report.Status != HealthStatus.Unhealthy;
            var body = new HealthResponse(
                up ? "healthy" : "unhealthy",
                report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString().ToLowerInvariant()));

            return Results.Json(body, statusCode: up ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        })
        .WithTags("Health")
        .AllowAnonymous()
        .Produces<HealthResponse>(StatusCodes.Status200OK)
        .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
