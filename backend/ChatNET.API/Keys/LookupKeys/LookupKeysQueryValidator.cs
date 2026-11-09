using ChatNET.API.Conversations.Membership;
using FluentValidation;

namespace ChatNET.API.Keys.LookupKeys;

public class LookupKeysQueryValidator : AbstractValidator<LookupKeysQuery>
{
    // The most people one conversation key is ever sealed for: a full group.
    public const int MaxUsers = GroupLimits.MaxActiveMembers;

    public LookupKeysQueryValidator()
    {
        RuleFor(x => x.UserIds)
            .NotEmpty().WithMessage("Name at least one person.")
            .Must(ids => ids.Count <= MaxUsers).WithMessage($"Name at most {MaxUsers} people.");

        RuleForEach(x => x.UserIds)
            .NotEqual(Guid.Empty).WithMessage("A user id must not be empty.");
    }
}
