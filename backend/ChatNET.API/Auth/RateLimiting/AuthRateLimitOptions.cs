using System.ComponentModel.DataAnnotations;

namespace ChatNET.API.Auth.RateLimiting;

// How many login or registration requests one client address may make per window. Bound from
// the "RateLimiting:Auth" configuration section and validated when the host starts.
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "RateLimiting:Auth";
    public const string PolicyName = "auth";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
