using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Membership;
using FluentValidation;

namespace ChatNET.API.Conversations.CreateGroup;

public class CreateGroupCommandValidator : AbstractValidator<CreateGroupCommand>
{
    public const int MaxMembers = GroupLimits.MaxOtherMembers;

    public CreateGroupCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(GroupLimits.MaxNameLength);

        RuleFor(x => x.MemberIds)
            .NotNull()
            .Must(ids => ids is { Count: > 0 and <= MaxMembers })
            .WithMessage($"A group needs between 1 and {MaxMembers} other members.");

        RuleForEach(x => x.MemberIds)
            .NotEmpty();

        RuleFor(x => x.Id).NotEmpty().WithMessage("The group needs an id.");

        RuleFor(x => x.Epoch).NotNull().WithMessage("The first epoch is required.").SetValidator(new EpochRequestValidator());
    }
}
