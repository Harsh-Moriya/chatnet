using FluentValidation;

namespace ChatNET.API.Conversations.CreateGroup;

public class CreateGroupCommandValidator : AbstractValidator<CreateGroupCommand>
{
    public const int MaxMembers = 100;

    public CreateGroupCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.MemberIds)
            .NotNull()
            .Must(ids => ids is { Count: > 0 and <= MaxMembers })
            .WithMessage($"A group needs between 1 and {MaxMembers} other members.");

        RuleForEach(x => x.MemberIds)
            .NotEmpty();
    }
}
