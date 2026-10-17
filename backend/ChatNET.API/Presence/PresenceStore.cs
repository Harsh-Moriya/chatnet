using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatNET.API.Presence;

public sealed class PresenceStore : IPresenceStore
{
    private const string KeyPrefix = "chatnet:presence:";

    private static readonly RedisKey ExpiringKey = KeyPrefix + "expiring";

    // Prepared once: the script text is parsed here, and only its parameters change per call.
    private static readonly LuaScript TouchScript = LuaScript.Prepare(PresenceScripts.Touch);
    private static readonly LuaScript SettleScript = LuaScript.Prepare(PresenceScripts.Settle);
    private static readonly LuaScript ExpiredScript = LuaScript.Prepare(PresenceScripts.Expired);

    private readonly RedisConnection _redis;
    private readonly PresenceOptions _options;

    public PresenceStore(RedisConnection redis, IOptions<PresenceOptions> options)
    {
        _redis = redis;
        _options = options.Value;
    }

    public async Task<TouchResult> TouchAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var db = await _redis.GetDatabaseAsync();
        var reply = (long[]?)await TouchScript.EvaluateAsync(db, TouchParameters(userId, connectionId)).WaitAsync(cancellationToken);

        return ToTouchResult(reply!);
    }

    public async Task<IReadOnlyList<TouchResult>> TouchManyAsync(
        IReadOnlyList<(Guid UserId, string ConnectionId)> connections, CancellationToken cancellationToken)
    {
        if (connections.Count == 0)
            return [];

        // One batch: all the commands go out together and the replies come back together, instead of a
        // round trip per connection.
        var db = await _redis.GetDatabaseAsync();
        var batch = db.CreateBatch();
        var pending = connections
            .Select(c => TouchScript.EvaluateAsync(batch, TouchParameters(c.UserId, c.ConnectionId)))
            .ToList();
        batch.Execute();

        var replies = await Task.WhenAll(pending).WaitAsync(cancellationToken);
        return replies.Select(r => ToTouchResult((long[])r!)).ToList();
    }

    public async Task<SettleResult> SettleAsync(
        Guid userId, string? connectionId, bool finish, CancellationToken cancellationToken)
    {
        var db = await _redis.GetDatabaseAsync();
        var reply = (long[]?)await SettleScript.EvaluateAsync(db, new
        {
            userKey = UserKey(userId),
            expiringKey = ExpiringKey,
            userId = userId.ToString(),
            connectionId = connectionId ?? string.Empty,
            finish = finish ? "1" : "0",
        }).WaitAsync(cancellationToken);

        var outcome = reply![0] switch
        {
            0 => SettleOutcome.StillOnline,
            1 => SettleOutcome.WentOffline,
            2 => SettleOutcome.NoConnectionsLeft,
            _ => SettleOutcome.AlreadyOffline,
        };

        return new SettleResult(outcome, FromMilliseconds(reply[1]));
    }

    public async Task<ExpiredUsers> GetExpiredAsync(int max, CancellationToken cancellationToken)
    {
        var db = await _redis.GetDatabaseAsync();
        var reply = (RedisResult[]?)await ExpiredScript.EvaluateAsync(db, new { expiringKey = ExpiringKey, max })
            .WaitAsync(cancellationToken);

        // { time, id, score, id, score ... }
        var users = new List<ExpiredUser>();
        for (var i = 1; i + 1 < reply!.Length; i += 2)
        {
            var expiresAt = (long)double.Parse((string)reply[i + 1]!, System.Globalization.CultureInfo.InvariantCulture);
            users.Add(new ExpiredUser(Guid.Parse((string)reply[i]!), FromMilliseconds(expiresAt - (long)_options.Ttl.TotalMilliseconds)));
        }

        return new ExpiredUsers(FromMilliseconds((long)reply[0]), users);
    }

    public async Task<PresenceSnapshot> GetSnapshotAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var db = await _redis.GetDatabaseAsync();
        var ids = userIds.ToList();

        // Both in one batch: the time the answer is as of, and the score of each user in the expiring
        // set (nothing for a user who is not in it).
        var batch = db.CreateBatch();
        var time = batch.ExecuteAsync("TIME");

        // Redis refuses a score lookup with no members, and nobody is a valid thing to ask about.
        var scores = ids.Count == 0
            ? null
            : batch.SortedSetScoresAsync(ExpiringKey, ids.Select(id => (RedisValue)id.ToString()).ToArray());
        batch.Execute();

        var timeReply = (long[]?)await time.WaitAsync(cancellationToken);
        var scoreReply = scores is null ? [] : await scores.WaitAsync(cancellationToken);

        var online = new HashSet<Guid>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (scoreReply[i].HasValue)
                online.Add(ids[i]);
        }

        return new PresenceSnapshot(FromMilliseconds(timeReply![0] * 1000 + timeReply[1] / 1000), online);
    }

    private object TouchParameters(Guid userId, string connectionId) => new
    {
        userKey = UserKey(userId),
        expiringKey = ExpiringKey,
        userId = userId.ToString(),
        connectionId,
        ttlMs = (long)_options.Ttl.TotalMilliseconds,

        // The user's key outlives the entries in it by a margin, so it cleans itself up if every server
        // dies, but never expires while the entries are still meant to count.
        keyTtlMs = (long)(_options.Ttl.TotalMilliseconds * 2),
    };

    private static RedisKey UserKey(Guid userId) => $"{KeyPrefix}user:{userId}";

    private static TouchResult ToTouchResult(long[] reply) => new(reply[0] == 1, FromMilliseconds(reply[1]));

    private static DateTime FromMilliseconds(long unixMilliseconds) =>
        DateTime.UnixEpoch.AddMilliseconds(unixMilliseconds);
}
