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
using Microsoft.AspNetCore.Hosting;
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

    // A mail catcher: the API sends through it as it would through a provider's relay, and the tests read what arrived.
    private readonly IContainer _mail;

    public const string StorageBucket = "chatnet-tests";

    public const string MailFromAddress = "noreply@chatnet.test";

    public ChatApiFactory()
    {
        _mail = new ContainerBuilder("axllent/mailpit:v1.33.0")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8025).ForPath("/api/v1/info")))
            .Build();

        _storage = new ContainerBuilder("rustfs/rustfs:1.0.3")
            .WithEnvironment("RUSTFS_ACCESS_KEY", _storageAccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", _storageSecretKey)
            .WithCommand("/data")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(9000).ForPath("/health")))
            .Build();
    }

    // Every host built from this factory counts its rate limits under keys of its own. They share one Redis, and a limit test
    // that also counted the requests other tests made from the same address would see them. A test that needs two hosts to
    // share their counts names one prefix for both, which outranks this one.
    //
    // No host has push keys unless a test gives it some (see PushApi.WithPush). The test host runs in the Development environment,
    // which reads the developer's user-secrets, and an environment variable cannot say "empty" (setting one to nothing removes it),
    // so the keys are named here as empty to keep a developer's own from switching push on in every test.
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RateLimiting:Store:KeyPrefix"] = $"t{Guid.NewGuid():N}",
                ["Push:PublicKey"] = string.Empty,
                ["Push:PrivateKey"] = string.Empty,
                ["Push:Subject"] = string.Empty,

                // The same for the login of an SMTP relay, which a developer may have in user-secrets. The test host talks to
                // the mail catcher, which takes no login, and a developer's real one would be refused with it.
                ["Email:Smtp:UserName"] = string.Empty,
                ["Email:Smtp:Password"] = string.Empty,
            }));

    // A client for the test bucket, to put objects where the API will look and to see what it left behind.
    public IAmazonS3 Storage { get; private set; } = null!;

    public string StorageServiceUrl => $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(9000)}";

    public string SmtpHost => _mail.Hostname;

    public int SmtpPort => _mail.GetMappedPublicPort(1025);

    // The mail the API sent to the catcher, found by recipient (see MailpitApi).
    public MailpitApi Mail { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _storage.StartAsync(), _mail.StartAsync());

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
        Environment.SetEnvironmentVariable("Email__Smtp__Host", SmtpHost);
        Environment.SetEnvironmentVariable("Email__Smtp__Port", SmtpPort.ToString());
        Environment.SetEnvironmentVariable("Email__Smtp__Security", "None");
        Environment.SetEnvironmentVariable("Email__From__Address", MailFromAddress);

        // Effectively disables the rate limits for ordinary tests, which register many users from one address and send, edit
        // and search many times as one person. Rate limit tests build a host with a tight limit.
        foreach (var policy in new[] { "Auth", "Send", "Uploads", "Edit", "Search", "Avatars", "Push", "Keys", "ForgotPassword", "ResetPassword", "PasswordResetEmail" })
            Environment.SetEnvironmentVariable($"RateLimiting__{policy}__PermitLimit", "1000000");

        // Presence timers are set so they never fire by themselves while other tests run: the refresh loop
        // ticks once an hour, entries last three, and a user is taken offline at once when their last
        // connection closes. Tests of those behaviours build a host with the values they need.
        Environment.SetEnvironmentVariable("Presence__RefreshInterval", "01:00:00");
        Environment.SetEnvironmentVariable("Presence__Ttl", "03:00:00");
        Environment.SetEnvironmentVariable("Presence__OfflineGrace", "00:00:00");

        // The outbox relay ticks once an hour so it never pushes by itself while other tests run; the request that wrote a
        // row has two minutes to push it before the relay may take it. Tests of the relay run a pass by hand and move
        // the clock.
        Environment.SetEnvironmentVariable("Outbox__RelayInterval", "01:00:00");
        Environment.SetEnvironmentVariable("Outbox__ClaimAfter", "00:02:00");

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

        Mail = new MailpitApi(new Uri($"http://{_mail.Hostname}:{_mail.GetMappedPublicPort(8025)}"));

        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        Storage?.Dispose();
        Mail?.Dispose();
        await _storage.DisposeAsync();
        await _mail.DisposeAsync();
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
            || config["Storage:ServiceUrl"] != StorageServiceUrl
            || config["Email:Smtp:Host"] != SmtpHost
            || config["Email:Smtp:Port"] != SmtpPort.ToString())
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
