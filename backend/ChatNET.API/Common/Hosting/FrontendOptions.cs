namespace ChatNET.API.Common.Hosting;

// Bound from the "Frontend" configuration section: where the web application is served. CORS allows this one origin, and the
// links in emails point at it. It is never taken from a request, whose Host header the sender controls.
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    public string Url { get; set; } = "http://localhost:5173";
}
