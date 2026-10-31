using ChatNET.API.Messaging;

namespace ChatNET.API.Tests.Messaging;

public class MessagingOptionsTests
{
    [Fact]
    public void The_default_edit_window_is_fifteen_minutes_and_accepted()
    {
        var options = new MessagingOptions();

        Assert.Equal(TimeSpan.FromMinutes(15), options.EditWindow);
        Assert.True(new MessagingOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    public void An_edit_window_that_is_not_positive_is_refused_and_named(string window)
    {
        var result = new MessagingOptionsValidator().Validate(null, new MessagingOptions { EditWindow = TimeSpan.Parse(window) });

        Assert.True(result.Failed);
        Assert.Contains("Messaging:EditWindow", result.FailureMessage);
    }
}
