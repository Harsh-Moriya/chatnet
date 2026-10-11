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
    }
}
