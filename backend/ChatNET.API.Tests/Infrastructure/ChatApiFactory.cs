using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Media;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ChatNET.API.Tests.Infrastructure;

// Starts the real API in-process (WebApplicationFactory) against a real Postgres, a real Redis and a
// real S3-compatible object store (RustFS), each in a throwaway Docker container (Testcontainers). Nothing is mocked: requests
// go through the full pipeline (routing, auth, MediatR, EF Core, SignalR backplane).
//
// One instance is shared by every integration test class through a collection fixture, so
// the containers start once per test run, not once per test.
public sealed class ChatApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    // The credentials are made up for each run; the container is the only thing that ever sees them.
    private readonly string _storageAccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
    private readonly string _storageSecretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private readonly IContainer _storage;

    public const string StorageBucket = "chatnet-tests";

    public ChatApiFactory()
    {
        _storage = new ContainerBuilder("rustfs/rustfs:1.0.3")
            .WithEnvironment("RUSTFS_ACCESS_KEY", _storageAccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", _storageSecretKey)
            .WithCommand("/data")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(9000).ForPath("/health")))
            .Build();
    }

    // A client for the test bucket, to put objects where the API will look and to see what it left behind.
    public IAmazonS3 Storage { get; private set; } = null!;

    public string StorageServiceUrl => $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(9000)}";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _storage.StartAsync());

        // Configuration is supplied through environment variables on purpose. Program.cs reads
        // some settings while the app is still being assembled, and the environment is the one
        // source guaranteed to be in place by then. It also outranks user-secrets, so a
        // developer's own secrets can never redirect the tests to a real database.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", _redis.GetConnectionString());
        Environment.SetEnvironmentVariable("Jwt__SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Storage__ServiceUrl", StorageServiceUrl);
        Environment.SetEnvironmentVariable("Storage__Bucket", StorageBucket);
        Environment.SetEnvironmentVariable("Storage__AccessKey", _storageAccessKey);
        Environment.SetEnvironmentVariable("Storage__SecretKey", _storageSecretKey);

        // Effectively disables the login and registration rate limit for ordinary tests, which
        // register many users from one address. Rate limit tests build a host with a tight limit.
        Environment.SetEnvironmentVariable("RateLimiting__Auth__PermitLimit", "1000000");

        // Presence timers are set so they never fire by themselves while other tests run: the refresh loop
        // ticks once an hour, entries last three, and a user is taken offline at once when their last
        // connection closes. Tests of those behaviours build a host with the values they need.
        Environment.SetEnvironmentVariable("Presence__RefreshInterval", "01:00:00");
        Environment.SetEnvironmentVariable("Presence__Ttl", "03:00:00");
        Environment.SetEnvironmentVariable("Presence__OfflineGrace", "00:00:00");

        AssertUsingTestContainers();

        // The bucket is made the way the compose setup job makes it, without the CORS and lifecycle rules, which
        // no test needs: the tests upload from the server side of things, not from a browser.
        Storage = S3ClientFactory.Create(new StorageOptions
        {
            ServiceUrl = StorageServiceUrl,
            Region = "auto",
            AccessKey = _storageAccessKey,
            SecretKey = _storageSecretKey,
        });
        await Storage.PutBucketAsync(new PutBucketRequest { BucketName = StorageBucket });

        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        Storage?.Dispose();
        await _storage.DisposeAsync();
    }

    // Registers a brand new user through the public API and returns their identity and token.
    // Every call uses unique credentials, so tests never collide and need no cleanup.
    public Task<TestUser> RegisterUserAsync(string prefix = "user")
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        return RegisterUserAsync($"{prefix}_{suffix}", $"{prefix} {suffix}");
    }

    // For tests that need to control the names, for example to search for them. The caller is
    // responsible for keeping the user name unique.
    public async Task<TestUser> RegisterUserAsync(string userName, string displayName)
    {
        var email = $"{userName}@example.test";

        // Satisfies the Identity rules (length, upper, lower, digit) with random content.
        var password = "Aa1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { userName, displayName, email, password });
        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>()
                   ?? throw new InvalidOperationException("Register returned an empty body.");

        return new TestUser(Guid.Parse(auth.UserId), userName, displayName, email, password, auth.Token);
    }

    // An HttpClient that sends the user's token on every request.
    public HttpClient CreateClientFor(TestUser user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    // Direct database access for arranging states the API cannot produce yet (a deactivated
    // user, a member who left, existing messages) and for asserting on what was stored.
    // Each call gets its own DbContext, as a request would.
    public async Task ExecuteDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> QueryDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // Fails fast if the running app is not using the test containers. A mistake here would mean
    // tests writing to a developer's real database.
    private void AssertUsingTestContainers()
    {
        var config = Services.GetRequiredService<IConfiguration>();

        if (config.GetConnectionString("DefaultConnection") != _postgres.GetConnectionString()
            || config.GetConnectionString("Redis") != _redis.GetConnectionString()
            || config["Storage:ServiceUrl"] != StorageServiceUrl)
        {
            throw new InvalidOperationException(
                "The API under test is not using the test containers. Refusing to run tests.");
        }
    }
}

public sealed record TestUser(
    Guid Id,
    string UserName,
    string DisplayName,
    string Email,
    string Password,
    string Token);
