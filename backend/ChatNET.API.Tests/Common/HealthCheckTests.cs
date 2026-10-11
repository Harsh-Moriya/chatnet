using System.Net;
using System.Text.Json;
using ChatNET.API.Common.Health;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChatNET.API.Tests.Common;

// /health is what a load balancer or an uptime monitor asks. A 200 must mean "this instance can do
// its job", which needs the database and Redis, not just a running process. Returning 503 when a
// dependency is down takes the instance out of rotation instead of letting it fail user requests.
[Collection(IntegrationTestCollection.Name)]
public class HealthCheckTests
{
    private readonly ChatApiFactory _factory;

    public HealthCheckTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### The endpoint #####

    [Fact]
    public async Task Healthy_when_postgres_and_redis_answer()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("healthy", body.GetProperty("checks").GetProperty("postgres").GetString());
        Assert.Equal("healthy", body.GetProperty("checks").GetProperty("redis").GetString());
    }

    [Fact]
    public async Task Needs_no_token()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Answers_503_and_names_the_failing_dependency_when_one_is_down()
    {
        using var host = WithChecks(("postgres", HealthCheckResult.Unhealthy("down")), ("redis", HealthCheckResult.Healthy()));
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("unhealthy", body.GetProperty("status").GetString());
        Assert.Equal("unhealthy", body.GetProperty("checks").GetProperty("postgres").GetString());
        Assert.Equal("healthy", body.GetProperty("checks").GetProperty("redis").GetString());
    }

    // The endpoint is public, so it must say which dependency is down and nothing about why: a
    // connection error can contain a host name, a user name or part of a connection string.
    [Fact]
    public async Task Never_reveals_why_a_check_failed()
    {
        const string secret = "Host=db.internal;Password=hunter2";
        using var host = WithChecks(("postgres", HealthCheckResult.Unhealthy(secret, new InvalidOperationException(secret))));
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.DoesNotContain("hunter2", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("db.internal", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_check_that_throws_counts_as_unhealthy_instead_of_failing_the_request()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration("redis", _ => new ThrowingCheck(), null, null));
            })));
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task A_check_that_hangs_is_cut_off_and_counts_as_unhealthy()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration(
                    "redis", _ => new HangingCheck(), null, null, TimeSpan.FromMilliseconds(300)));
            })));
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void The_checks_the_app_registers_are_all_cut_off_after_a_few_seconds()
    {
        var registrations = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.Equivalent(new[] { "postgres", "redis" }, registrations.Select(r => r.Name));
        Assert.All(registrations, r => Assert.InRange(r.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
    }

    // ##### The real checks #####

    [Fact]
    public async Task The_postgres_check_is_unhealthy_when_the_database_cannot_be_reached()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2")
            .Options;
        await using var db = new AppDbContext(options);

        var result = await new PostgresHealthCheck(db).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task The_postgres_check_is_healthy_against_the_real_database()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await new PostgresHealthCheck(db).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task The_redis_check_is_unhealthy_when_redis_cannot_be_reached()
    {
        await using var check = new RedisHealthCheck("127.0.0.1:1,connectTimeout=500,abortConnect=false");

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task The_redis_check_is_healthy_against_the_real_redis_and_keeps_working()
    {
        var connection = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!;
        await using var check = new RedisHealthCheck(connection);

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, first.Status);
        Assert.Equal(HealthStatus.Healthy, second.Status);
    }

    // ##### Helpers #####

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithChecks(
        params (string Name, HealthCheckResult Result)[] checks) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                foreach (var (name, result) in checks)
                    options.Registrations.Add(new HealthCheckRegistration(name, _ => new FixedCheck(result), null, null));
            })));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class FixedCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class HangingCheck : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return HealthCheckResult.Healthy();
        }
    }
}
