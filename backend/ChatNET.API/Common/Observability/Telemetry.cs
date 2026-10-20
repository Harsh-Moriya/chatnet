using System.Diagnostics;

namespace ChatNET.API.Common.Observability;

// The source of the spans this application creates itself. ASP.NET Core, Npgsql and SignalR have
// their own sources; this one adds a span per MediatR request, so a trace reads
// HTTP request -> handler -> database.
public static class Telemetry
{
    public const string SourceName = "ChatNET";

    public static readonly ActivitySource Source = new(SourceName);
}
