using System.ComponentModel.DataAnnotations;

namespace ChatNET.API.RateLimiting;

// How many requests one person (or one client address, for the auth policies) may make per window in one policy. Bound from the
// policy's own section, such as "RateLimiting:Send", and validated when the host starts. There are no defaults here: the values
// are in appsettings.json, so the numbers can be read and changed in one place.
public sealed class RateLimitPolicyOptions
{
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; }

    // A day at most: the window is how long Redis holds a person's requests, and each one is a member of a sorted set.
    [Range(1, 86_400)]
    public int WindowSeconds { get; set; }
}
