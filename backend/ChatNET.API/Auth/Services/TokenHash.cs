using System.Security.Cryptography;
using System.Text;

namespace ChatNET.API.Auth.Services;

// The stored form of a token that is handed to someone (a refresh token, a password reset token). Each is 32 random bytes
// (256 bits), which cannot be guessed, so a plain SHA-256 is enough: a slow password hash would protect nothing extra here and
// would add cost to every use.
public static class TokenHash
{
    public static string Of(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
