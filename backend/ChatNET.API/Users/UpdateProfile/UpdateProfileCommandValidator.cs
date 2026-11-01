using FluentValidation;

namespace ChatNET.API.Users.UpdateProfile;

public class UpdateProfileCommandValidator : AbstractValidator<UpdateProfileCommand>
{
    // The limit registration has, so a name that could be chosen then can be kept now. NotEmpty also refuses a name of only
    // spaces.
    public const int MaxDisplayNameLength = 100;

    public UpdateProfileCommandValidator()
    {
        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .WithMessage("Say what you want to be called.")
            .MaximumLength(MaxDisplayNameLength);
    }
}
