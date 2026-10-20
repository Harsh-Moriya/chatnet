using ChatNET.API.Common.Auth;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace ChatNET.API.Common.Logging;

public static class LoggingSetup
{
    private const string DevelopmentTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    // Used before the host exists, so a failure while the app is starting (a missing setting, a bad
    // option) still produces a log line instead of a bare crash.
    public static Serilog.ILogger CreateStartupLogger() => new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    // Levels and overrides come from the "Serilog" section of configuration, so they can be changed
    // per environment without a rebuild. The sink is chosen here: readable lines for a developer,
    // one compact JSON object per line elsewhere, which is what a log collector (or `docker logs`)
    // wants to read.
    //
    // preserveStaticLogger keeps the host's logger separate from Log.Logger. Without it a second host
    // in the same process (every integration test builds one) fails with "the logger is already
    // frozen". writeToProviders sends the same events to any other registered logger provider, which
    // is how the tests read what the application logged.
    public static IHostBuilder UseChatNetLogging(this IHostBuilder host) => host.UseSerilog(
        (context, services, logger) =>
        {
            logger
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "chatnet-api")
                .Filter.ByExcluding(LogNoiseFilters.IsRefusedHubCall);

            if (context.HostingEnvironment.IsDevelopment())
                logger.WriteTo.Console(outputTemplate: DevelopmentTemplate);
            else
                logger.WriteTo.Console(new RenderedCompactJsonFormatter());
        },
        preserveStaticLogger: true,
        writeToProviders: true);

    // One line per request. The path is logged without its query string, which matters because the
    // SignalR client sends the access token in the query of the hub requests. The user is added by ID
    // only. /health is probed every few seconds by an orchestrator, so it is written at Verbose and
    // never shown at the configured levels.
    public static IApplicationBuilder UseChatNetRequestLogging(this IApplicationBuilder app)
        => app.UseSerilogRequestLogging(options =>
        {
            // The middleware writes to the static Log.Logger unless told otherwise, and the host keeps its
            // own logger apart from it (see UseChatNetLogging).
            options.Logger = app.ApplicationServices.GetRequiredService<Serilog.ILogger>();

            options.MessageTemplate = "HTTP {RequestMethod:l} {RequestPath:l} responded {StatusCode} in {Elapsed:0.0000} ms";

            options.GetLevel = (http, _, exception) =>
                http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : exception is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : LogEventLevel.Information;

            options.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                if (http.User.TryGetUserId(out var userId))
                    diagnostics.Set("UserId", userId);
            };
        });
}
