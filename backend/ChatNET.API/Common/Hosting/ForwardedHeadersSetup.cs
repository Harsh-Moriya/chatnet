using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Common.Hosting;

// Behind a reverse proxy that ends TLS, the API is reached over plain http from the proxy's address. Without
// help it would see "http" (so it would redirect to https for ever and never send HSTS) and the proxy's address
// for every client (so the login rate limit would count all clients together). The proxy says who the client was
// and which scheme it used in X-Forwarded-For and X-Forwarded-Proto, and this puts that into the request.
//
// Those headers can be sent by anyone, so they are believed only from the networks in TrustedProxyOptions. A
// client that could choose its own address could make every request look like a new client and walk past the
// rate limit.
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services)
    {
        services.AddOptions<TrustedProxyOptions>()
            .BindConfiguration(TrustedProxyOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TrustedProxyOptions>, TrustedProxyOptionsValidator>();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<TrustedProxyOptions>>((options, trusted) =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // The defaults trust the loopback address; only what is configured is trusted here.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var network in trusted.Value.KnownNetworks)
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));

                // One proxy in front of the API, so the last address in the chain is the client's and anything
                // a client wrote before it is not read.
                options.ForwardLimit = 1;
            });

        return services;
    }

    // Does nothing unless a network is configured: the headers of a request that came straight from a client
    // are never believed.
    public static WebApplication UseTrustedProxies(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value.KnownNetworks.Count > 0)
            app.UseForwardedHeaders();

        return app;
    }
}
