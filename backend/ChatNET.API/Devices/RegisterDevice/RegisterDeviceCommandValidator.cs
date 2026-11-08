using FluentValidation;

namespace ChatNET.API.Devices.RegisterDevice;

public class RegisterDeviceCommandValidator : AbstractValidator<RegisterDeviceCommand>
{
    public RegisterDeviceCommandValidator()
    {
        RuleFor(x => x.Kind).MustBeKind();
        RuleFor(x => x.Name).MustBeName();
    }
}
