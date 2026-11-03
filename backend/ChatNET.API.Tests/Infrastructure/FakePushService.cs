using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatNET.API.Tests.Infrastructure;

// One request as a push service received it.
public sealed record RecordedPush(Uri Uri, HttpMethod Method, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;

    // The alert, read the way the browser that holds the keys reads it.
    public JsonElement AlertFor(Browser browser) =>
        JsonDocument.Parse(WebPushDecryption.Decrypt(Body, browser)).RootElement.Clone();
}

// Stands in for the push services of the browser vendors: it takes the place of the network under the sender, records every
// request and answers as a test says. Nothing is posted anywhere.
public sealed class FakePushService : HttpMessageHandler
{
    private readonly List<RecordedPush> _received = [];

    // Answers every request unless a test changes it. A real push service answers 201 for a push it has taken.
    public Func<RecordedPush, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Created);

    public IReadOnlyList<RecordedPush> Received
    {
        get
        {
            lock (_received)
                return _received.ToList();
        }
    }

    public IReadOnlyList<RecordedPush> ReceivedBy(Browser browser) =>
        Received.Where(r => r.Uri.ToString() == browser.Endpoint).ToList();

    public static Func<RecordedPush, HttpResponseMessage> Answer(HttpStatusCode status) => _ => new HttpResponseMessage(status);

    // Answers by the endpoint the push was sent to, for tests where each browser has its own outcome.
    public void Answer(Browser browser, HttpStatusCode status)
    {
        var before = Respond;
        Respond = push => push.Uri.ToString() == browser.Endpoint ? new HttpResponseMessage(status) : before(push);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);

        var push = new RecordedPush(request.RequestUri!, request.Method, headers, body);
        lock (_received)
            _received.Add(push);

        return Respond(push);
    }
}

// What a browser does with a push (RFC 8291, with the record format of RFC 8188): derive the key from its own private key, the
// sender's public key that comes in the header and the authentication secret, and open the one record. A test that gets the
// alert back this way knows the keys were used the right way round, which nothing short of a real browser would tell it.
public static class WebPushDecryption
{
    private const int HeaderFixedLength = 16 + 4 + 1;

    public static string Decrypt(byte[] body, Browser browser)
    {
        var salt = body[..16];
        var keyIdLength = body[20];
        var senderPublic = body[HeaderFixedLength..(HeaderFixedLength + keyIdLength)];
        var record = body[(HeaderFixedLength + keyIdLength)..];

        var browserPublic = Base64Url.DecodeFromChars(browser.P256dh);
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });
        var shared = browser.Key.DeriveRawSecretAgreement(sender.PublicKey);

        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(browserPublic).Concat(senderPublic).ToArray();
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, shared, browser.AuthSecret), 32, keyInfo);

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var ciphertext = record[..^16];
        var tag = record[^16..];
        var plain = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(nonce, ciphertext, tag, plain);

        // The data, then the delimiter 0x02 that marks the last record, then any zero padding.
        var end = plain.Length;
        while (end > 0 && plain[end - 1] == 0)
            end--;

        if (end == 0 || plain[end - 1] != 0x02)
            throw new InvalidOperationException("The record does not end with the delimiter of the last record.");

        return Encoding.UTF8.GetString(plain, 0, end - 1);
    }
}

// The signed token of a VAPID request (RFC 8292): "Authorization: vapid t=<token>, k=<public key>".
public sealed record VapidToken(string PublicKey, JsonElement Claims, bool SignatureIsValid)
{
    public static VapidToken Read(string authorization)
    {
        var parts = authorization["vapid ".Length..].Split(", ");
        var token = parts.Single(p => p.StartsWith("t=", StringComparison.Ordinal))[2..];
        var publicKey = parts.Single(p => p.StartsWith("k=", StringComparison.Ordinal))[2..];

        var segments = token.Split('.');
        var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(segments[1])).RootElement.Clone();

        var point = Base64Url.DecodeFromChars(publicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
        var valid = verifier.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"), Base64Url.DecodeFromChars(segments[2]), HashAlgorithmName.SHA256);

        return new VapidToken(publicKey, claims, valid);
    }
}
