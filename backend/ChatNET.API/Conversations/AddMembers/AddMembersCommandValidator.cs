using ChatNET.API.Conversations.Membership;
using FluentValidation;

namespace ChatNET.API.Conversations.AddMembers;

public class AddMembersCommandValidator : AbstractValidator<AddMembersCommand>
{
    public AddMembersCommandValidator()
    {
        RuleFor(x => x.UserIds)
            .NotNull()
            .Must(ids => ids is { Count: > 0 and <= GroupLimits.MaxOtherMembers })
            .WithMessage($"Name between 1 and {GroupLimits.MaxOtherMembers} people to add.");

        RuleForEach(x => x.UserIds)
            .NotEmpty();
    }
}
