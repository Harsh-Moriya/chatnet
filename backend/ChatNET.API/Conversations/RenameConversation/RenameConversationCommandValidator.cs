using ChatNET.API.Conversations.Membership;
using FluentValidation;

namespace ChatNET.API.Conversations.RenameConversation;

// The same rule as the name a group is created with.
public class RenameConversationCommandValidator : AbstractValidator<RenameConversationCommand>
{
    public RenameConversationCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("A group needs a name.")
            .MaximumLength(GroupLimits.MaxNameLength);
    }
}
