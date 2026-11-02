namespace ChatNET.API.RateLimiting;

// Counts the requests of one key (a person, or a client address, in one policy) over the last `window` and says whether
// one more may go ahead. A refused request is not counted, so asking again while limited does not extend the wait.
public interface ISlidingWindowStore
{
    Task<RateLimitDecision> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken);
}
