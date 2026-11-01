namespace ChatNET.API.Common.Middleware;

// Adds response headers that tell browsers and caches how to treat what the API sends. They are set
// in OnStarting so they are in place whatever produces the response: an endpoint, the exception
// handler, the rate limiter or a bare status code.
//
// There is no Content-Security-Policy here. The API returns JSON, which a browser does not execute,
// and the page that does execute (the Scalar docs in development) loads scripts from elsewhere. A
// policy belongs to whatever serves the frontend.
public class SecurityHeadersMiddleware
{
    private static readonly string[] NoStorePrefixes = ["/api", "/hubs", "/health"];

    private static readonly PathString Pictures = new("/api/avatars");

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            // A browser must not guess a different content type than the one sent.
            headers.XContentTypeOptions = "nosniff";

            // Nothing here is meant to be shown inside another site's frame.
            headers.XFrameOptions = "DENY";

            // Links followed from a page that came from the API do not reveal where they came from.
            headers["Referrer-Policy"] = "no-referrer";

            // Responses are about one signed-in person. A shared cache, or the browser's back button
            // after sign-out, must not be able to show them to someone else. The one exception is a picture that was
            // found: its address holds a random file name that is new for every picture, so what is served there never
            // changes, and the endpoint says how long it may be kept. Anything else from there, a 404 included, is not
            // kept.
            var servedPicture = context.Response.StatusCode == StatusCodes.Status200OK
                && context.Request.Path.StartsWithSegments(Pictures);

            if (!servedPicture && NoStorePrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
                headers.CacheControl = "no-store";

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
