using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Infrastructure;

// A clock a test moves by hand. The application reads the time through TimeProvider wherever the time
// changes what happens (does a refresh token still work, is a replaced one being reused), so a test can
// jump forward two weeks instead of waiting for them.
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class ManualTimeProviderExtensions
{
    // A host whose application logic reads the given clock. The access token is still checked against
    // the system clock, which is the point: only the refresh logic moves.
    public static WebApplicationFactory<Program> WithClock(this WebApplicationFactory<Program> factory, ManualTimeProvider clock)
        => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
}
