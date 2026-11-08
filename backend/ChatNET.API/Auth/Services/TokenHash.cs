using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace ChatNET.API.Auth.Services;

// The secrets that are handed to someone (a refresh token, a password reset token) and their stored form. Each is 32 random bytes
// (256 bits), which cannot be guessed, so a plain SHA-256 is enough for the stored copy: a slow password hash would protect nothing
// extra here and would add cost to every use.
public static class TokenHash
{
    // 32 random bytes in base64url, which is 43 characters.
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Of(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
