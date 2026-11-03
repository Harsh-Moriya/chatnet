using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ChatNET.API.Push.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ChatNET.API.Tests.Infrastructure;

// What a test needs to play both sides of Web Push: a server with keys, and a browser that has a key pair and a secret of its
// own, as the push API of a real browser makes them.
public static class PushApi
{
    // Made once for the run, the way an operator would make them for a deployment. A host that is given them has push on; the
    // shared test host has none, so the tests of everything else are not changed by it.
    public static readonly VapidSettings Vapid = MakeVapid();

    private static VapidSettings MakeVapid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);

        return new VapidSettings(
            Base64Url.EncodeToString([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]),
            Base64Url.EncodeToString(parameters.D!),
            "mailto:push@example.test");
    }

    // A host with push set up, and the settings a test wants to change on top.
    public static WebApplicationFactory<Program> WithPush(
        this ChatApiFactory factory, Action<IWebHostBuilder>? configure = null, IDictionary<string, string?>? settings = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Push:PublicKey"] = Vapid.PublicKey,
                    ["Push:PrivateKey"] = Vapid.PrivateKey,
                    ["Push:Subject"] = Vapid.Subject,
                });

                if (settings is not null)
                    config.AddInMemoryCollection(settings);
            });
            configure?.Invoke(builder);
        });

    public static HttpClient ClientFor(this WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    public static Task<HttpResponseMessage> PutSubscriptionAsync(this HttpClient client, Browser browser) =>
        client.PutAsJsonAsync("/api/push/subscriptions", browser.AsSubscriptionJson());

    public static Task<HttpResponseMessage> DeleteSubscriptionAsync(this HttpClient client, string endpoint) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/push/subscriptions")
        {
            Content = JsonContent.Create(new { endpoint }),
        });

    public static Task<List<PushSubscription>> SubscriptionsOfAsync(this ChatApiFactory factory, Guid userId) =>
        factory.QueryDbAsync(db => db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync());

    public static Task<List<PushSubscription>> SubscriptionsAtAsync(this ChatApiFactory factory, string endpoint) =>
        factory.QueryDbAsync(db => db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == endpoint).ToListAsync());

    // Puts a subscription straight into the table, for the states the API does not produce (an address that is no longer
    // allowed, a browser last used a long time ago).
    public static async Task<PushSubscription> AddSubscriptionAsync(
        this ChatApiFactory factory, Guid userId, Browser browser, DateTime? lastUsedAt = null)
    {
        var now = DateTime.UtcNow;
        var row = new PushSubscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Endpoint = browser.Endpoint,
            P256dh = browser.P256dh,
            Auth = browser.Auth,
            CreatedAt = now,
            LastUsedAt = lastUsedAt ?? now,
        };
        await factory.ExecuteDbAsync(async db =>
        {
            db.PushSubscriptions.Add(row);
            await db.SaveChangesAsync();
        });

        return row;
    }

    public static Task RemoveSubscriptionsAsync(this ChatApiFactory factory) =>
        factory.ExecuteDbAsync(db => db.PushSubscriptions.ExecuteDeleteAsync());
}

// The keys of the server as an operator would make and set them.
public sealed record VapidSettings(string PublicKey, string PrivateKey, string Subject);

// A browser that subscribed: the address its vendor's push service gave it, and the key pair and secret it keeps to read what
// is pushed. A test that holds the private key can decrypt what the server sent, which is how it checks the payload.
public sealed class Browser
{
    private Browser(string endpoint, ECDiffieHellman key, byte[] authSecret)
    {
        Endpoint = endpoint;
        Key = key;
        AuthSecret = authSecret;
    }

    public string Endpoint { get; }

    public ECDiffieHellman Key { get; }

    public byte[] AuthSecret { get; }

    // The uncompressed point and the secret, base64url without padding, as the browser hands them to a page.
    public string P256dh
    {
        get
        {
            var q = Key.ExportParameters(false).Q;
            return Base64Url.EncodeToString([0x04, .. q.X!, .. q.Y!]);
        }
    }

    public string Auth => Base64Url.EncodeToString(AuthSecret);

    // The token in the path is what makes each endpoint its own, as a real one is.
    public static Browser Subscribe(string host = "fcm.googleapis.com") => new(
        $"https://{host}/fcm/send/{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
        ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
        RandomNumberGenerator.GetBytes(16));

    // The shape of PushSubscription.toJSON() in a browser.
    public object AsSubscriptionJson() => new { endpoint = Endpoint, keys = new { p256dh = P256dh, auth = Auth } };
}
