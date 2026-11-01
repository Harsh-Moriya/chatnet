using StackExchange.Redis;

namespace ChatNET.API.Presence;

// One Redis multiplexer for the life of the application, connected on first use. The multiplexer
// is the expensive part (a connection, a handshake) and is meant to be shared. It is created with
// AbortOnConnectFail off so a Redis that is down when the application starts is retried by the
// library instead of stopping the application, and a connection attempt that failed is forgotten so
// the next call tries again.
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly ConfigurationOptions _options;
    private readonly object _gate = new();
    private Task<IConnectionMultiplexer>? _connection;

    public RedisConnection(string connectionString)
    {
        _options = ConfigurationOptions.Parse(connectionString);
        _options.AbortOnConnectFail = false;
    }

    public async Task<IDatabase> GetDatabaseAsync() => (await ConnectionAsync()).GetDatabase();

    // For publishing to and listening on a channel. A subscription lives on the shared multiplexer, which sets it up
    // again by itself when the connection to Redis comes back.
    public async Task<ISubscriber> GetSubscriberAsync() => (await ConnectionAsync()).GetSubscriber();

    private Task<IConnectionMultiplexer> ConnectionAsync()
    {
        lock (_gate)
        {
            if (_connection is null || _connection.IsFaulted)
                _connection = ConnectAsync();

            return _connection;
        }
    }

    private async Task<IConnectionMultiplexer> ConnectAsync() => await ConnectionMultiplexer.ConnectAsync(_options);

    public async ValueTask DisposeAsync()
    {
        if (_connection is { IsCompletedSuccessfully: true })
            await _connection.Result.DisposeAsync();
    }
}
