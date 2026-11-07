namespace ChatNET.API.RateLimiting;

// The names of the policies endpoints are limited by, and the configuration section that holds the limit of each.
public static class RateLimitPolicies
{
    // Counted per client address: signing in, registering, renewing and ending a session have no signed-in person to count, and
    // neither has a request for a picture, which an <img> makes without a token. Renewing is its own policy, so a session being
    // renewed never uses up the allowance for signing in, and the pictures have a count of their own: a page of them is many
    // requests at once. Asking for a reset link and using one are anonymous too, each with a count of its own.
    public const string Auth = "auth";
    public const string Refresh = "refresh";
    public const string Avatars = "avatars";
    public const string ForgotPassword = "forgot-password";
    public const string ResetPassword = "reset-password";

    // Counted per signed-in person.
    public const string Send = "send";
    public const string Uploads = "uploads";
    public const string Edit = "edit";
    public const string Search = "search";
    public const string Push = "push";

    // Counted per email address, by the handler that sends the reset email, and not by the pipeline: the address is in the body,
    // which has not been read when the pipeline decides. It is not a policy an endpoint names, so the setup does not define it.
    public const string PasswordResetEmail = "password-reset-email";

    public static readonly IReadOnlyList<(string Name, string Section)> All =
    [
        (Auth, "RateLimiting:Auth"),
        (Refresh, "RateLimiting:Refresh"),
        (Send, "RateLimiting:Send"),
        (Uploads, "RateLimiting:Uploads"),
        (Edit, "RateLimiting:Edit"),
        (Search, "RateLimiting:Search"),
        (Avatars, "RateLimiting:Avatars"),
        (Push, "RateLimiting:Push"),
        (ForgotPassword, "RateLimiting:ForgotPassword"),
        (ResetPassword, "RateLimiting:ResetPassword"),
        (PasswordResetEmail, "RateLimiting:PasswordResetEmail"),
    ];
}
