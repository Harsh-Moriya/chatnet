using FluentValidation;

namespace ChatNET.API.Messaging.EditMessage;

// What can be said about an edit without looking at the message: the new text is a message of a shape the formats allow, and it is
// the first edit or a later one. Whether it is the next one is for the repository to say.
public class EditMessageCommandValidator : AbstractValidator<EditMessageCommand>
{
    public EditMessageCommandValidator()
    {
        RuleFor(x => x.Ciphertext)
            .NotEmpty()
            .Must(ciphertext => MessageBlob.Decode(ciphertext) is not null)
            .WithMessage("The message is not a well formed encrypted message.");

        // The first version of a message is edit count 0, so the first edit is 1.
        RuleFor(x => x.EditCount)
            .NotNull()
            .GreaterThanOrEqualTo(1);

        RuleFor(x => x.MentionedUserIds)
            .Must(ids => ids is null || ids.Count <= MentionLimits.Max)
            .WithMessage($"A message can name at most {MentionLimits.Max} people.");

        RuleForEach(x => x.MentionedUserIds).NotEqual(Guid.Empty);
    }
}
