namespace ChatNET.API.Push.Persistence;

public interface IPushSubscriptionRepository
{
    // Registers a browser for a person. A browser that is already registered (the page renews it every time it opens, and
    // another person may sign in on the same browser) keeps its row and takes the new owner and keys. A person who ends up
    // with more than "maxPerUser" loses the ones used least recently, never the one just saved.
    Task SaveAsync(
        Guid userId, string endpoint, string p256dh, string auth, int maxPerUser, DateTime now,
        CancellationToken cancellationToken);

    // Removes the caller's own registration of this browser. Another person's is left alone, and nothing says whether it
    // exists. True when a row was removed.
    Task<bool> RemoveAsync(Guid userId, string endpoint, CancellationToken cancellationToken);
}
