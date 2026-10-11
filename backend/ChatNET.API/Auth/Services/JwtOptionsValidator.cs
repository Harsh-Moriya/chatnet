using System.Text;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.Services;

// Runs when the host starts (ValidateOnStart), so a bad configuration stops the application
// immediately with a message naming the setting, rather than surfacing as an obscure error
// when the first token is issued. Messages never include the key itself.
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    // HS256 signs with HMAC-SHA256, whose security level is that of a 256-bit key. A shorter key
    // is a weaker key, which RFC 7518 forbids for this algorithm.
    private const int MinimumKeyBytes = 32;

    private const string KeyHint =
        "It must be at least 32 bytes (256 bits). Set it with: " +
        "dotnet user-secrets set \"Jwt:SigningKey\" \"$(openssl rand -base64 32)\"";

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
            failures.Add("Jwt:Issuer is not configured.");

        if (string.IsNullOrWhiteSpace(options.Audience))
            failures.Add("Jwt:Audience is not configured.");

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            failures.Add($"Jwt:SigningKey is not configured. {KeyHint}");
        }
        else
        {
            var keyBytes = Encoding.UTF8.GetByteCount(options.SigningKey);
            if (keyBytes < MinimumKeyBytes)
                failures.Add($"Jwt:SigningKey is only {keyBytes} bytes long. {KeyHint}");
        }

        if (options.ExpiryDays <= 0)
            failures.Add("Jwt:ExpiryDays must be greater than zero.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
