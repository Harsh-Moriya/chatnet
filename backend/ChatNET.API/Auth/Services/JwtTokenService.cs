using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ChatNET.API.Auth.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ChatNET.API.Auth.Services;

public class JwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public AccessToken GenerateToken(AppUser user)
    {
        var claims = new[]
        {
            // Sub (subject) is the standard JWT claim for the user's unique identifier.
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName ?? string.Empty),
            new Claim("display_name", user.DisplayName),
            // Jti (JWT ID) makes each token unique.
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        // The system clock, not the injected TimeProvider: the token is checked against the system clock
        // by the JWT middleware and by SignalR, so it has to be issued by it.
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
        );

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

// A signed access token and the moment it stops being accepted.
public record AccessToken(string Value, DateTime ExpiresAt);
