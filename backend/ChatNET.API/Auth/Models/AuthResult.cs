namespace ChatNET.API.Auth.Models;

// Endpoints choose how to deliver the refresh credential. Browser responses serialize only Response.
public record AuthResult(AuthResponse Response, IssuedRefreshToken RefreshToken, Guid? DeviceId = null);

// A refresh token as it must be given to the client: the secret value, and when it stops working.
public record IssuedRefreshToken(string Value, DateTime ExpiresAt);
