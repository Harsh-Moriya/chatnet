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

    public int ExpiryDays { get; set; } = 7;
}
