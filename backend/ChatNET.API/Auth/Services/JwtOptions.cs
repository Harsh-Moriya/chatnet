namespace ChatNET.API.Auth.Services;

// The "Jwt" configuration section as a typed object. Token creation and token validation both
// read these values, so they cannot drift apart, and JwtOptionsValidator checks them once
// when the application starts instead of failing later on the first request.
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    // Secret. Supplied through user-secrets or an environment variable, never appsettings.json.
    public string SigningKey { get; set; } = string.Empty;

    // How long an access token lives. Short, because a signed token cannot be taken back: the shorter it
    // is, the less a stolen or deactivated user's token is worth. The refresh token renews it.
    public int AccessTokenMinutes { get; set; } = 15;

    // How long a refresh token lives. Every refresh issues a new one, so a person who uses the app at
    // least once in this time stays signed in.
    public int RefreshTokenDays { get; set; } = 14;

    // How long after a refresh token was replaced it may be presented again without it counting as
    // theft. Two tabs that refresh at the same moment each send the same token, and the second one
    // arrives after the first replaced it.
    public int RefreshReuseGraceSeconds { get; set; } = 30;
}
