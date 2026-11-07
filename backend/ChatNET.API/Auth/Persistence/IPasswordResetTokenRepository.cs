using ChatNET.API.Auth.Models;

namespace ChatNET.API.Auth.Persistence;

public interface IPasswordResetTokenRepository
{
    // Stores a token. When the save fails nothing stays tracked, so the same context can go on to save other changes: the
    // outbox relay handles a whole pass with one context.
    Task AddAsync(PasswordResetToken token, CancellationToken cancellationToken);

    // Marks the link as used and returns the person it belongs to, or null when no link with this hash can be used (unknown,
    // already used, or expired: the three are not told apart). One conditional statement, so of any number of requests with
    // the same link exactly one gets the person. A link is usable while now is before its expiry. It runs in the caller's
    // transaction, so a reset that fails afterwards puts the link back.
    Task<Guid?> TryConsumeAsync(string tokenHash, DateTime now, CancellationToken cancellationToken);

    // Marks every link of the person that is still unused as used, so a reset voids the links that were also waiting.
    Task VoidUnusedAsync(Guid userId, DateTime now, CancellationToken cancellationToken);

    // Removes links that expired before the given moment. Returns how many.
    Task<int> DeleteExpiredBeforeAsync(DateTime expiredBefore, CancellationToken cancellationToken);
}
