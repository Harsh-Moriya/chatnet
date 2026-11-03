using Microsoft.Extensions.Options;

namespace ChatNET.API.Push;

// Decides whether the server may post to an address a browser gave as its push endpoint. The server makes that request
// itself, so an address of the caller's choosing would let any signed-in person make it call any host it can reach (an
// internal service, the machine's metadata address). Only https on the default port, on a host of the allowed push
// services or a subdomain of one, passes. It is asked when an endpoint is registered and again before every push, so a
// row saved under an older list, or by other means, is never posted to.
public sealed class PushEndpointPolicy
{
    private readonly string[] _hosts;

    public PushEndpointPolicy(IOptions<PushOptions> options)
    {
        _hosts = options.Value.AllowedHosts.Select(host => host.ToLowerInvariant()).ToArray();
    }

    public bool IsAllowed(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;

        // A user name in the address ("https://allowed.example@evil.example/") makes the host something else than it reads.
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return false;

        var host = uri.IdnHost.ToLowerInvariant();

        // Equal, or a subdomain: the dot in front is what keeps "evilpush.example.com" from matching "push.example.com".
        return _hosts.Any(allowed => host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal));
    }
}
