using ChatNET.API.Presence;
using StackExchange.Redis;

namespace ChatNET.API.Messaging.Hubs;

// Listens, on every server instance, for the message that says a person's connections no longer belong in a conversation, and
// acts on the connections this instance holds (see ConnectionEvictor). Redis being down when the application starts is not
// a reason to stop it: the listener keeps trying, and once it is subscribed the multiplexer renews the subscription
// by itself whenever the connection to Redis comes back.
public sealed class ConnectionEvictionListener : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly RedisConnection _redis;
    private readonly ConnectionEvictor _evictor;
    private readonly ILogger<ConnectionEvictionListener> _logger;
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConnectionEvictionListener(
        RedisConnection redis, ConnectionEvictor evictor, ILogger<ConnectionEvictionListener> logger)
    {
        _redis = redis;
        _evictor = evictor;
        _logger = logger;
    }

    // Completes once this instance is listening. A message published before that is not heard, so a test that evicts right
    // after a host starts waits for it.
    public Task Subscribed => _subscribed.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var subscriber = await _redis.GetSubscriberAsync();
                await subscriber.SubscribeAsync(ConnectionEvictor.Channel, OnMessage);
                _subscribed.TrySetResult();
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not listen for connections to evict; trying again");
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    // Runs on the Redis client's thread, so it only starts the work.
    private void OnMessage(RedisChannel channel, RedisValue message)
    {
        if (!ConnectionEvictor.TryDecode(message, out var userId, out var conversationId))
        {
            _logger.LogWarning("Ignored a message on the eviction channel that is not a user and a conversation");
            return;
        }

        _ = EvictAsync(userId, conversationId);
    }

    private async Task EvictAsync(Guid userId, Guid conversationId)
    {
        try
        {
            await _evictor.EvictLocalAsync(userId, conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not take the connections of user {UserId} out of conversation {ConversationId}",
                userId, conversationId);
        }
    }
}
