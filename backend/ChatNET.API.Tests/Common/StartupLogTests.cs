using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Common;

// A running instance's log is where an operator looks first, so an ordinary start must not put warnings in it. Each
// warning here was found by reading the log of the real containers (note 22) and would be there on every start of
// every instance.
[Collection(IntegrationTestCollection.Name)]
public class StartupLogTests
{
    private readonly ChatApiFactory _factory;

    public StartupLogTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // As deployed: Production, behind a proxy that ends TLS, with the first request a health check over plain http.
    [Fact]
    public async Task An_instance_behind_a_proxy_logs_no_warning_when_it_starts_and_answers_a_health_check()
    {
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" }));
            builder.ConfigureTestServices(services => services.AddLogging(logging => logging.AddProvider(capture)));
        });
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var noisy = capture.Entries
            .Where(e => e.StartsWith("[Warning]") || e.StartsWith("[Error]") || e.StartsWith("[Critical]"))
            .Select(e => e.Length > 200 ? e[..200] : e)
            .ToList();
        Assert.True(noisy.Count == 0, "The start logged: " + string.Join(" || ", noisy));
    }

    // Nothing in the application protects data with a data protection key (D-217). The framework nevertheless
    // creates the first key when the host starts, and the first thing that asks for the data protection provider (the
    // token providers Identity builds the first time someone signs up or in) makes it look for somewhere to keep
    // keys; in a container it finds nowhere and warns, on every instance. The watcher below notices a key being
    // stored, the repository being read, and the provider being asked for. None of them is needed, so none should
    // happen, wherever the application runs.
    [Fact]
    public async Task The_key_ring_is_never_touched_when_an_instance_starts_or_when_people_sign_up_and_in()
    {
        var watcher = new KeyRingWatcher();
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<KeyManagementOptions>(options => options.XmlRepository = watcher);
                services.Replace(ServiceDescriptor.Singleton<IDataProtectionProvider>(_ =>
                {
                    watcher.ProviderWasAskedFor();
                    return new EphemeralDataProtectionProvider();
                }));
            }));
        using var client = host.CreateClient();

        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        Assert.False(watcher.Touched, "The start touched the key ring.");

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var credentials = new { email = $"dp_{suffix}@example.test", password = "Aa1" + suffix };
        (await client.PostAsJsonAsync("/api/auth/register", new
        {
            userName = $"dp_{suffix}", displayName = "Key Ring", credentials.email, credentials.password,
        })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).EnsureSuccessStatusCode();

        Assert.False(watcher.Touched, "Signing up or in touched the key ring.");
    }

    private sealed class KeyRingWatcher : IXmlRepository
    {
        private readonly ConcurrentQueue<XElement> _stored = new();
        private int _reads;
        private int _providerAsks;

        public bool Touched => _reads > 0 || _providerAsks > 0 || !_stored.IsEmpty;

        public void ProviderWasAskedFor() => Interlocked.Increment(ref _providerAsks);

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            Interlocked.Increment(ref _reads);
            return _stored.ToArray();
        }

        public void StoreElement(XElement element, string friendlyName) => _stored.Enqueue(element);
    }
}
