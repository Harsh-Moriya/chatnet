using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatNET.API.Push;

// Signs the requests to the push services (VAPID, RFC 8292). A push service takes the signed token as proof that the request
// comes from the server whose public key the browser subscribed with, and may show the subject to its operator to contact whoever
// runs the sender. The token names the push service it is for (the audience, the origin of the endpoint), so one that leaks is
// good for that service only, and for twelve hours at most.
public sealed class VapidSigner : IDisposable
{
    // The RFC allows up to 24 hours.
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private static readonly string Header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);

    private readonly ECDsa _key;
    private readonly string _publicKey;
    private readonly string _subject;
    private readonly TimeProvider _time;

    // ECDsa is not promised to be safe to share between threads, and a relay pass sends in parallel.
    private readonly object _signing = new();

    public VapidSigner(string publicKey, string privateKey, string subject, TimeProvider time)
    {
        _key = VapidKeys.CreateSigner(publicKey, privateKey);
        _publicKey = publicKey;
        _subject = subject;
        _time = time;
    }

    // The Authorization header for a request to this endpoint: "vapid t=<token>, k=<public key>".
    public AuthenticationHeaderValue AuthorizationFor(Uri endpoint)
    {
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = (_time.GetUtcNow() + Lifetime).ToUnixTimeSeconds(),
            ["sub"] = _subject,
        }));

        var signed = $"{Header}.{claims}";
        byte[] signature;
        lock (_signing)
            signature = _key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256);

        return new AuthenticationHeaderValue("vapid", $"t={signed}.{Base64Url.EncodeToString(signature)}, k={_publicKey}");
    }

    public void Dispose() => _key.Dispose();
}
