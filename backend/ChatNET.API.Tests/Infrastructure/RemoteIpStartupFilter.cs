using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace ChatNET.API.Tests.Infrastructure;

// The in-memory test server has no real network connection, so every request would appear to
// come from the same unknown address. This test-only middleware lets a test choose the client
// address per request through the X-Test-Remote-Ip header, which is what makes it possible to
// verify behaviour that is partitioned by client address.
public sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value)
                && IPAddress.TryParse(value, out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return nextMiddleware(context);
        });

        next(app);
    };
}
