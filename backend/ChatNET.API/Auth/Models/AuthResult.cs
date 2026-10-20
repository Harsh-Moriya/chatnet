namespace ChatNET.API.Auth.Models;

// What a handler hands to the endpoint after a sign-in, registration or refresh: the body to return and
// the refresh token to put in the cookie. Kept apart so the token cannot end up in the body by accident.
public record AuthResult(AuthResponse Response, IssuedRefreshToken RefreshToken);

// A refresh token as it must be given to the client: the secret value, and when it stops working.
public record IssuedRefreshToken(string Value, DateTime ExpiresAt);
