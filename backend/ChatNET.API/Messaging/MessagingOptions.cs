using Microsoft.Extensions.Options;

namespace ChatNET.API.Messaging;

// Bound from the "Messaging" configuration section and validated when the host starts.
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    // How long after it was sent its sender may still edit a message. Deleting has no limit. The limit stops someone
    // rewriting what others have since answered; a typo is noticed within minutes.
    public TimeSpan EditWindow { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingOptions options)
        => options.EditWindow > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Messaging:EditWindow must be greater than zero.");
}
