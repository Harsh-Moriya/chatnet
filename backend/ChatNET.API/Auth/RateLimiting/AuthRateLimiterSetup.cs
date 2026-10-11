using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.RateLimiting;

// Defines the "auth" rate limit policy. It is an IConfigureOptions so the numbers come from the
// validated AuthRateLimitOptions; AddRateLimiter's own callback cannot reach dependency
// injection.
public sealed class AuthRateLimiterSetup : IConfigureOptions<RateLimiterOptions>
{
    private readonly AuthRateLimitOptions _limits;

    public AuthRateLimiterSetup(IOptions<AuthRateLimitOptions> limits)
    {
        _limits = limits.Value;
    }

    public void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // One counter per client address, so one abusive client uses up only its own allowance.
        // A fixed window is simple and cheap; its known weakness is that a client can use the
        // end of one window and the start of the next, briefly doubling the rate. For
        // brute-force protection that is acceptable, and account lockout covers the rest.
        options.AddPolicy(AuthRateLimitOptions.PolicyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = _limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(_limits.WindowSeconds),

                    // Requests over the limit are rejected at once instead of queued.
                    QueueLimit = 0,
                }));

        options.OnRejected = WriteRejectionAsync;
    }

    // Answers with the same ProblemDetails shape as every other error, plus a Retry-After header
    // so well-behaved clients know when to try again.
    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        int? retryAfterSeconds = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            http.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString();
        }

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too Many Requests",
            Detail = retryAfterSeconds is { } seconds
                ? $"Too many attempts. Try again in {seconds} seconds."
                : "Too many attempts. Try again later.",
        };

        await http.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(
            new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }
}
