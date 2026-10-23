using System.Net;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Common.Hosting;

// The networks the reverse proxy in front of the API connects from, in CIDR form (for example 172.16.0.0/12 for
// Docker's private networks). Empty, which is the default, means there is no proxy to trust and the forwarded
// headers a client sends are ignored.
public class TrustedProxyOptions
{
    public const string SectionName = "ForwardedHeaders";

    public List<string> KnownNetworks { get; set; } = [];
}

public class TrustedProxyOptionsValidator : IValidateOptions<TrustedProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, TrustedProxyOptions options)
    {
        var invalid = options.KnownNetworks.Where(network => !IPNetwork.TryParse(network, out _)).ToList();

        return invalid.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"ForwardedHeaders:KnownNetworks must list networks in CIDR form such as 172.16.0.0/12; not understood: {string.Join(", ", invalid)}.");
    }
}
