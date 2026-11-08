using FluentValidation;

namespace ChatNET.API.Devices.Linking.FindLinkRequest;

public class FindLinkRequestQueryValidator : AbstractValidator<FindLinkRequestQuery>
{
    public FindLinkRequestQueryValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .Must(LinkCodes.IsCode)
            .WithMessage("The link code must be 10 characters from 0123456789ABCDEFGHJKMNPQRSTVWXYZ.");
    }
}
