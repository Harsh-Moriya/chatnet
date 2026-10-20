namespace ChatNET.API.Auth.Models;

// One refresh token, as stored. The token itself is never stored, only its SHA-256 hash, so a copy of
// the table cannot be used to sign in.
//
// Tokens form a family: signing in starts one, and every refresh replaces the token with a new one in the
// same family. That chain is what lets a stolen token be noticed. The thief and the owner each hold a
// token from the same chain; the first to refresh retires the other's, and when the retired token is
// presented again the whole family is revoked.
public class RefreshToken
{
    // Created by the application, not the database, because the token that replaces this one has to
    // refer to it before either is saved.
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid FamilyId { get; set; }

    // Lowercase hex SHA-256 of the token. Unique, and the column a refresh looks the token up by.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    // Set when the token stops being usable: it was replaced by a refresh, or its family was revoked
    // (sign-out, reuse detected, account deactivated).
    public DateTime? RevokedAt { get; set; }

    // Set only when a refresh replaced this token, and it tells that apart from a revocation. A token
    // presented again just after being replaced is a tab that lost a race; a long time after, it is
    // reuse. Not a foreign key: the rows of a family are removed in order of age, and nothing reads the
    // successor through this value.
    public Guid? ReplacedById { get; set; }

    public AppUser? User { get; set; }
}
