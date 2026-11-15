using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ChatNET.API.Tests.Calls;

// Hosts for the tests of calls that are about time: a ring that runs out in a second, a grace of two, a lease of four, and a round of
// looking after calls every second by itself. (The shared host has none of that: its rounds never run unless a test runs one.)
public static class CallHosts
{
    public static Dictionary<string, string?> QuickSettings(Dictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Calls:RingTimeout"] = "00:00:01",
            ["Calls:ReconnectGrace"] = "00:00:02",
            ["Calls:Lease"] = "00:00:04",
            ["Calls:RenewInterval"] = "00:00:01",
            ["Calls:TickInterval"] = "00:00:01",
        };

        foreach (var (key, value) in more ?? [])
            settings[key] = value;

        return settings;
    }

    public static WebApplicationFactory<Program> QuickCalls(this ChatApiFactory factory, Dictionary<string, string?>? more = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(QuickSettings(more))));

    // The same with push on and the pushes going to a fake service that records them.
    public static WebApplicationFactory<Program> QuickCallsWithPush(
        this ChatApiFactory factory, Action<IWebHostBuilder> configure, Dictionary<string, string?>? more = null) =>
        factory.WithPush(configure, QuickSettings(more));
}
