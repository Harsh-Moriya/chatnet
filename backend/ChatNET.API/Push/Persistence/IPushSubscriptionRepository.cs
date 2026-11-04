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

    // Every browser these people have registered.
    Task<List<PushTarget>> GetForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    // The browsers a push reached: they were used, which keeps them from being removed as unused.
    Task MarkUsedAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken cancellationToken);

    // Browsers the push service said are gone, or that may no longer be posted to.
    Task RemoveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    // Registrations not renewed or pushed to since the cutoff. Returns how many.
    Task<int> RemoveUnusedSinceAsync(DateTime cutoff, CancellationToken cancellationToken);

    // The people a new message alerts when they are away: active members of the conversation other than the sender, who have
    // not muted it and have at least one browser registered, leaving out the people in "excluded" (the ones the message names,
    // who are alerted by the mention, which also reaches a muted conversation).
    Task<List<Guid>> FindMessageRecipientsAsync(
        Guid conversationId, Guid senderId, IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken);
}
