using System.ComponentModel.DataAnnotations;

namespace ChatNET.API.Auth.RateLimiting;

// How many refresh and sign-out requests one client address may make per window. Looser than the login
// limit: a refresh happens on its own about every quarter of an hour per open tab, many people can share
// one address (an office, a school), and a refresh needs a token that cannot be guessed, so there is
// nothing to brute-force. The limit is only there to stop a client hammering the endpoint.
public sealed class RefreshRateLimitOptions
{
    public const string SectionName = "RateLimiting:Refresh";
    public const string PolicyName = "refresh";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 60;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
