using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ChatNET.API.Push.Persistence;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Push;

// Posts an alert to the push service of a browser: the payload is encrypted to the browser's keys (WebPushEncryption) and the
// request is signed with this server's key (VapidSigner). What is decided here is what each answer of the push service means for
// the registration.
//
// Nothing logged here carries the address or the text: the address is a capability, and only the ids of our own rows and the
// status the service answered with say what happened.
public sealed class WebPushSender : IPushSender, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly VapidSigner? _signer;
    private readonly int _timeToLiveSeconds;
    private readonly PushEndpointPolicy _endpoints;
    private readonly ILogger<WebPushSender> _logger;

    // The HttpClient is the one of this type's registration: it has the timeout and does not follow redirects, so a push
    // service cannot send the server on to another address.
    public WebPushSender(
        HttpClient http, IOptions<PushOptions> options, PushEndpointPolicy endpoints, TimeProvider time,
        ILogger<WebPushSender> logger)
    {
        _http = http;
        _endpoints = endpoints;
        _logger = logger;

        // A server without keys never sends, and must still be able to build everything that holds a sender.
        var settings = options.Value;
        if (!settings.Enabled)
            return;

        _signer = new VapidSigner(settings.PublicKey, settings.PrivateKey, settings.Subject, time);
        _timeToLiveSeconds = (int)settings.TimeToLive.TotalSeconds;
    }

    public void Dispose() => _signer?.Dispose();

    public async Task<PushOutcome> SendAsync(PushTarget target, PushAlert alert, CancellationToken cancellationToken)
    {
        if (_signer is null)
            throw new InvalidOperationException("Push is not set up on this server.");

        // Asked before every push and not only when the address is registered: a row saved under an older list, or by other
        // means, must never make the server call a host that is not a push service.
        if (!_endpoints.IsAllowed(target.Endpoint))
        {
            _logger.LogWarning("Push subscription {SubscriptionId} names a host that is not allowed; it is removed", target.Id);
            return PushOutcome.Gone;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(alert, Json);
        if (payload.Length > WebPushEncryption.MaxPlaintextLength)
            throw new InvalidOperationException("An alert is longer than a push can carry.");

        HttpRequestMessage request;
        try
        {
            request = Build(target, payload);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or PlatformNotSupportedException)
        {
            // The keys of the registration cannot be used to encrypt (a point that is not on the curve, for example). Windows
            // reports that as PlatformNotSupportedException, other systems as CryptographicException.
            _logger.LogWarning(
                "Push subscription {SubscriptionId} has keys that cannot be used ({FailureType}); it is removed", target.Id, ex.GetType().Name);
            return PushOutcome.Gone;
        }

        using (request)
        {
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken);
                return Classify(target, response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Push to subscription {SubscriptionId} could not reach the push service ({FailureType})", target.Id, ex.GetType().Name);
                return PushOutcome.Unavailable;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The client's own timeout; a cancellation from the caller is passed on.
                _logger.LogWarning("Push to subscription {SubscriptionId} timed out", target.Id);
                return PushOutcome.Unavailable;
            }
        }
    }

    private HttpRequestMessage Build(PushTarget target, byte[] payload)
    {
        var body = WebPushEncryption.Encrypt(
            payload, Base64Url.DecodeFromChars(target.P256dh), Base64Url.DecodeFromChars(target.Auth));

        var request = new HttpRequestMessage(HttpMethod.Post, target.Endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");

        request.Headers.Authorization = _signer!.AuthorizationFor(request.RequestUri!);
        request.Headers.TryAddWithoutValidation("TTL", _timeToLiveSeconds.ToString(CultureInfo.InvariantCulture));

        // A person writing to someone is the reason a phone wakes up: without this a push service may hold it back.
        request.Headers.TryAddWithoutValidation("Urgency", "high");

        return request;
    }

    private PushOutcome Classify(PushTarget target, HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300)
            return PushOutcome.Sent;

        // The push service says the browser is gone: unsubscribed, or its subscription expired.
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return PushOutcome.Gone;

        // Not the browser's fault and not ours to fix by trying at once: the service is busy or down.
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500)
        {
            _logger.LogWarning("Push to subscription {SubscriptionId} was not taken by the push service ({StatusCode})", target.Id, (int)status);
            return PushOutcome.Unavailable;
        }

        // Anything else (a refused key, a malformed request) will answer the same next time. Not a reason to remove the
        // registration: a server started with the wrong key would otherwise remove everybody's.
        _logger.LogWarning("Push to subscription {SubscriptionId} was refused by the push service ({StatusCode})", target.Id, (int)status);
        return PushOutcome.Rejected;
    }
}
