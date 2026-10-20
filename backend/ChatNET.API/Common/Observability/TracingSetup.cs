using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ChatNET.API.Common.Observability;

public static class TracingSetup
{
    private const string ServiceNameKey = "OTEL_SERVICE_NAME";
    private const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    // Traces are exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set (for example to the local Jaeger
    // at http://localhost:4317). Without it the instrumentation still runs but nothing leaves the
    // process, so tests and plain local runs send nothing anywhere. The sampler is left to the standard
    // OTEL_TRACES_SAMPLER and OTEL_TRACES_SAMPLER_ARG settings; the default keeps every trace whose
    // caller did not decide otherwise.
    //
    // The ASP.NET Core instrumentation also listens to the SignalR server's own spans, so hub calls are
    // traced without listing that source here.
    public static IServiceCollection AddChatNetTracing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(configuration[ServiceNameKey] ?? "chatnet-api"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                        // Probes are noise in a trace list.
                        options.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddSource(Telemetry.SourceName)
                    .AddProcessor<UrlQueryRedactionProcessor>();

                if (!string.IsNullOrWhiteSpace(configuration[EndpointKey]))
                    tracing.AddOtlpExporter();
            });

        return services;
    }
}
