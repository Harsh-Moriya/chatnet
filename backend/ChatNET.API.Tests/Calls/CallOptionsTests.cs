using ChatNET.API.Calls;

namespace ChatNET.API.Tests.Calls;

// The limits of a live call are validated when the host starts: a lease that one late renewal can outlast would end calls that are fine.
public class CallOptionsTests
{
    private static readonly CallOptionsValidator Validator = new();

    [Fact]
    public void The_defaults_are_valid_and_are_the_ones_the_product_promises()
    {
        var options = new CallOptions();

        Assert.True(Validator.Validate(null, options).Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RingTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ReconnectGrace);
    }

    [Theory]
    [InlineData(nameof(CallOptions.RingTimeout), 0)]
    [InlineData(nameof(CallOptions.ReconnectGrace), 0)]
    [InlineData(nameof(CallOptions.TickInterval), 0)]
    [InlineData(nameof(CallOptions.EndingRetry), 0)]
    public void A_duration_that_is_not_positive_is_refused_and_named(string setting, int seconds)
    {
        var options = new CallOptions();
        typeof(CallOptions).GetProperty(setting)!.SetValue(options, TimeSpan.FromSeconds(seconds));

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains($"Calls:{setting}", result.FailureMessage);
    }

    [Fact]
    public void A_lease_shorter_than_two_renewals_is_refused()
    {
        var options = new CallOptions { RenewInterval = TimeSpan.FromSeconds(10), Lease = TimeSpan.FromSeconds(19) };

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Calls:Lease must be at least twice Calls:RenewInterval", result.FailureMessage);
    }

    [Fact]
    public void A_lease_of_exactly_two_renewals_is_valid()
    {
        var options = new CallOptions { RenewInterval = TimeSpan.FromSeconds(10), Lease = TimeSpan.FromSeconds(20) };

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Renewing_more_often_than_the_tick_is_refused()
    {
        var options = new CallOptions { TickInterval = TimeSpan.FromSeconds(2), RenewInterval = TimeSpan.FromSeconds(1) };

        Assert.Contains("Calls:RenewInterval", Validator.Validate(null, options).FailureMessage);
    }

    [Theory]
    [InlineData(nameof(CallOptions.MaxSignalBytes))]
    [InlineData(nameof(CallOptions.MaxSignalsPerCall))]
    public void A_limit_below_one_is_refused(string setting)
    {
        var options = new CallOptions();
        typeof(CallOptions).GetProperty(setting)!.SetValue(options, 0);

        Assert.Contains($"Calls:{setting}", Validator.Validate(null, options).FailureMessage);
    }
}
