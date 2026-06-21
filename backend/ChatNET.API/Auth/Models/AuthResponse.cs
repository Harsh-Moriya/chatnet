namespace ChatNET.API.Auth.Models;

public record AuthResponse(
    string Token,
    string UserId,
    string UserName,
    string DisplayName
);
