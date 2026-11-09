using ChatNET.API.Conversations.KeyEpochs;
using FluentValidation;

namespace ChatNET.API.Conversations.CreateDirect;

public class CreateDirectCommandValidator : AbstractValidator<CreateDirectCommand>
{
    public CreateDirectCommandValidator()
    {
        RuleFor(x => x.OtherUserId)
            .NotEmpty()
            .NotEqual(x => x.UserId)
            .WithMessage("You cannot start a conversation with yourself.");

        RuleFor(x => x.Id).NotEmpty().WithMessage("The conversation needs an id.");

        RuleFor(x => x.Epoch).NotNull().WithMessage("The first epoch is required.").SetValidator(new EpochRequestValidator());
    }
}
