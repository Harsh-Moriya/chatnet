using FluentValidation;

namespace ChatNET.API.Conversations.SetMuted;

public class SetMutedCommandValidator : AbstractValidator<SetMutedCommand>
{
    public SetMutedCommandValidator()
    {
        RuleFor(x => x.IsMuted).NotNull().WithMessage("Say whether the conversation is muted.");
    }
}
