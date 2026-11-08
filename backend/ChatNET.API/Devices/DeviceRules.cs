using ChatNET.API.Devices.Models;
using ChatNET.API.Devices.Persistence;
using FluentValidation;

namespace ChatNET.API.Devices;

// What a client may say about itself, checked the same way wherever a device describes itself (registering, asking to be linked).
public static class DeviceRules
{
    public static bool IsKind(string? value) =>
        Enum.TryParse<DeviceKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind) && !int.TryParse(value, out _);

    public static DeviceKind ParseKind(string value) => Enum.Parse<DeviceKind>(value, ignoreCase: true);

    // The name as it is stored: without the spaces around it. Text to display and nothing else, so only control characters
    // (which have no place in a name) and an empty or very long name are refused.
    public static string CleanName(string value) => value.Trim();

    // Each stops at its first failure, so an empty value gets one message and not one for every rule.
    public static IRuleBuilderOptions<T, string?> MustBeKind<T>(this IRuleBuilderInitial<T, string?> rule) =>
        rule.Cascade(CascadeMode.Stop).NotEmpty().Must(IsKind).WithMessage("The device kind must be Web, Ios or Android.");

    public static IRuleBuilderOptions<T, string?> MustBeName<T>(this IRuleBuilderInitial<T, string?> rule) =>
        rule.Cascade(CascadeMode.Stop).NotEmpty()
            .Must(name => name is not null && CleanName(name).Length is > 0 and <= DeviceConfiguration.MaxNameLength)
            .WithMessage($"The device name must be 1 to {DeviceConfiguration.MaxNameLength} characters.")
            .Must(name => name is not null && !name.Any(char.IsControl))
            .WithMessage("The device name must not contain control characters.");
}
