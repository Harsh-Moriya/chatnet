using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace ChatNET.API.Common.Health;

// Healthy when Redis answers a PING. It keeps one multiplexer of its own for the life of the app:
// connecting on every probe would cost a handshake each time, and a multiplexer created with
// AbortOnConnectFail off reconnects by itself, so the check recovers as soon as Redis does.
public sealed class RedisHealthCheck : IHealthCheck, IAsyncDisposable
{
    private readonly ConfigurationOptions _options;
    private readonly object _gate = new();
    private Task<IConnectionMultiplexer>? _connection;

    public RedisHealthCheck(string connectionString)
    {
        _options = ConfigurationOptions.Parse(connectionString);
        _options.AbortOnConnectFail = false;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var multiplexer = await ConnectionAsync().WaitAsync(cancellationToken);
            await multiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis did not answer a ping.", ex);
        }
    }

    // A failed attempt to connect is forgotten, so the next probe tries again.
    private Task<IConnectionMultiplexer> ConnectionAsync()
    {
        lock (_gate)
        {
            if (_connection is null || _connection.IsFaulted)
                _connection = ConnectAsync();

            return _connection;
        }
    }

    private async Task<IConnectionMultiplexer> ConnectAsync() =>
        await ConnectionMultiplexer.ConnectAsync(_options);

    public async ValueTask DisposeAsync()
    {
        if (_connection is { IsCompletedSuccessfully: true })
            await _connection.Result.DisposeAsync();
    }
}
