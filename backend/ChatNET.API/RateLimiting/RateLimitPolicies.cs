namespace ChatNET.API.RateLimiting;

// The names of the policies endpoints are limited by, and the configuration section that holds the limit of each.
public static class RateLimitPolicies
{
    // Counted per client address: signing in, registering, renewing and ending a session have no signed-in person to count, and
    // neither has a request for a picture, which an <img> makes without a token. Renewing is its own policy, so a session being
    // renewed never uses up the allowance for signing in, and the pictures have a count of their own: a page of them is many
    // requests at once.
    public const string Auth = "auth";
    public const string Refresh = "refresh";
    public const string Avatars = "avatars";

    // Counted per signed-in person.
    public const string Send = "send";
    public const string Uploads = "uploads";
    public const string Edit = "edit";
    public const string Search = "search";

    public static readonly IReadOnlyList<(string Name, string Section)> All =
    [
        (Auth, "RateLimiting:Auth"),
        (Refresh, "RateLimiting:Refresh"),
        (Send, "RateLimiting:Send"),
        (Uploads, "RateLimiting:Uploads"),
        (Edit, "RateLimiting:Edit"),
        (Search, "RateLimiting:Search"),
        (Avatars, "RateLimiting:Avatars"),
    ];
}
