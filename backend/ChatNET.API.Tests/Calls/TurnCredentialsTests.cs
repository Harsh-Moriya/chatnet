using ChatNET.API.Calls.Ice;
using ChatNET.API.Tests.Infrastructure;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Calls;

// A real TURN server, started the way the stack starts it (docker/turn/start.sh and turnserver.conf, with a secret in the environment),
// and a real TURN client (turnutils_uclient, which ships in the image) asking it for an allocation with the credential the API makes.
// This is what shows that the HMAC is the one the server computes: the pinned value in IceServersTests only shows that two
// implementations of the same formula agree.
public sealed class CoturnFixture : IAsyncLifetime
{
    public const string Secret = "9f1c0b7a5d3e48f2a6b1c9d07e5a3f4b8c2d6e0a1b3c5d7e9f0a2b4c6d8e0f13";

    private readonly IContainer _container;

    public CoturnFixture()
    {
        var turn = Path.Combine(RepositoryRoot(), "docker", "turn");

        _container = new ContainerBuilder("coturn/coturn:4.18.0")
            .WithEntrypoint("/bin/sh", "/etc/chatnet-turn/start.sh")
            .WithEnvironment("TURN_SECRET", Secret)
            .WithResourceMapping(new FileInfo(Path.Combine(turn, "start.sh")), "/etc/chatnet-turn/")
            .WithResourceMapping(new FileInfo(Path.Combine(turn, "turnserver.conf")), "/etc/chatnet-turn/")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Total auth threads"))
            .Build();
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // What the server says about a credential: the allocation was made (exit code 0), or it was not.
    public async Task<(bool Allocated, string Output)> AllocateAsync(string username, string credential)
    {
        var address = _container.IpAddress;
        var result = await _container.ExecAsync(
            ["turnutils_uclient", "-u", username, "-w", credential, "-e", address, "-m", "1", "-n", "1", "-l", "100", "-p", "3478", address]);

        return (result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker", "turn", "start.sh")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("The repository's docker/turn folder was not found above the test assembly.");
    }
}

public class TurnCredentialsTests : IClassFixture<CoturnFixture>
{
    private static readonly Guid Ana = Guid.Parse("01a1ddf1-d39e-7903-a7f1-42c7632aa24b");

    private readonly CoturnFixture _relay;

    public TurnCredentialsTests(CoturnFixture relay)
    {
        _relay = relay;
    }

    private static IceServerDto TurnEntry(string secret, DateTimeOffset now)
    {
        var options = new TurnOptions { Secret = secret, Urls = ["turn:localhost:3478?transport=udp"] };
        return new IceServers(Options.Create(options), new ManualTimeProvider(now)).For(Ana).Last();
    }

    [Fact]
    public async Task The_relay_accepts_the_credential_the_API_makes_and_refuses_the_ones_it_should()
    {
        var now = DateTimeOffset.UtcNow;
        var good = TurnEntry(CoturnFixture.Secret, now);

        // One that is made under another secret, one whose time has passed (made two hours ago, valid for an hour), and one for another
        // person's username with this person's credential. Each takes the client about fifteen seconds to give up on, so they run together.
        var wrongSecret = TurnEntry(new string('x', 64), now);
        var expired = TurnEntry(CoturnFixture.Secret, now.AddHours(-2));
        var tampered = good with { Username = good.Username![..^1] + (good.Username![^1] == 'a' ? 'b' : 'a') };

        var accepted = _relay.AllocateAsync(good.Username!, good.Credential!);
        var refused = await Task.WhenAll(
            new[] { ("another secret", wrongSecret), ("an expired username", expired), ("a changed username", tampered) }
                .Select(async attempt => (attempt.Item1, await _relay.AllocateAsync(attempt.Item2.Username!, attempt.Item2.Credential!))));

        var allocation = await accepted;
        Assert.True(allocation.Allocated, allocation.Output);
        foreach (var (name, result) in refused)
        {
            Assert.False(result.Allocated, $"The relay allocated for {name}.");
            Assert.Contains("Cannot complete Allocation", result.Output);
        }
    }
}
