using System.Threading.RateLimiting;
using ChatNET.API.Common.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ChatNET.API.RateLimiting;

// Defines every rate limit policy. It is an IConfigureOptions so the numbers come from the validated options and the counts from
// the shared store; AddRateLimiter's own callback cannot reach dependency injection.
public sealed class RateLimiterSetup : IConfigureOptions<RateLimiterOptions>
{
    private readonly IOptionsMonitor<RateLimitPolicyOptions> _limits;
    private readonly string _keyPrefix;
    private readonly ISlidingWindowStore _store;

    public RateLimiterSetup(
        IOptionsMonitor<RateLimitPolicyOptions> limits, IOptions<RateLimitStoreOptions> store, ISlidingWindowStore windows)
    {
        _limits = limits;
        _keyPrefix = store.Value.KeyPrefix;
        _store = windows;
    }

    public void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // One count per client address, so one abusive client uses up only its own allowance.
        AddPolicy(options, RateLimitPolicies.Auth, ClientAddress);
        AddPolicy(options, RateLimitPolicies.Refresh, ClientAddress);

        // One count per signed-in person, so many people behind one address (an office, a school) do not share a budget and
        // one person cannot use several addresses to get more. A request without a person (a token that does not check out
        // is answered with 401 after this) is counted by its address.
        AddPolicy(options, RateLimitPolicies.Send, SignedInPerson);
        AddPolicy(options, RateLimitPolicies.Uploads, SignedInPerson);
        AddPolicy(options, RateLimitPolicies.Edit, SignedInPerson);
        AddPolicy(options, RateLimitPolicies.Search, SignedInPerson);

        options.OnRejected = WriteRejectionAsync;
    }

    private void AddPolicy(RateLimiterOptions options, string policy, Func<HttpContext, string> whoIsCounted)
    {
        var limits = _limits.Get(policy);
        var window = TimeSpan.FromSeconds(limits.WindowSeconds);

        options.AddPolicy(policy, http =>
            RateLimitPartition.Get(
                $"{_keyPrefix}:{policy}:{whoIsCounted(http)}",
                key => new SlidingWindowLimiter(_store, key, limits.PermitLimit, window)));
    }

    private static string ClientAddress(HttpContext http) => $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    private static string SignedInPerson(HttpContext http) =>
        http.User.TryGetUserId(out var userId) ? $"user:{userId:N}" : ClientAddress(http);

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
                ? $"Too many requests. Try again in {seconds} seconds."
                : "Too many requests. Try again later.",
        };

        await http.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(
            new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }
}
