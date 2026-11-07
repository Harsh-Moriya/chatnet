namespace ChatNET.API.RateLimiting;

// The answer to "may this request go ahead": yes, or no and how long until the oldest request in the window leaves it.
// StoreUnavailable is true when the shared store could not be asked and the request was let through.
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter = default, bool StoreUnavailable = false);
