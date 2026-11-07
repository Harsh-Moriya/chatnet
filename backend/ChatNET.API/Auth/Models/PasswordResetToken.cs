namespace ChatNET.API.Auth.Models;

// One password reset link, as stored. Like a refresh token, the token itself is never stored, only its SHA-256 hash, so a copy
// of the table cannot be used to reset anyone's password. A link is single use: using it sets UsedAt, and so does a
// later reset of the same account, which voids every link that was still waiting.
public class PasswordResetToken
{
    // Created by the application, not the database.
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    // Lowercase hex SHA-256 of the token. Unique, and the column a reset looks the token up by.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    // Set when the link was used, or voided by a reset made with another link. A link with a value here is never accepted.
    public DateTime? UsedAt { get; set; }

    public AppUser? User { get; set; }
}
