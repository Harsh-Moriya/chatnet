using FluentValidation;

namespace ChatNET.API.Conversations.ChangeMemberRole;

public class ChangeMemberRoleCommandValidator : AbstractValidator<ChangeMemberRoleCommand>
{
    public ChangeMemberRoleCommandValidator()
    {
        RuleFor(x => x.Role)
            .NotNull().WithMessage("Say which role the person should have.")
            .IsInEnum().WithMessage("The role must be Owner, Admin or Member.");
    }
}
