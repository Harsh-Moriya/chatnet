using ChatNET.API.Messaging.SendMessage;
using FluentValidation;

namespace ChatNET.API.Messaging.EditMessage;

// Whether the text may be empty depends on the message (one with files may), so that rule is the handler's. What
// can be said without looking at the message is said here.
public class EditMessageCommandValidator : AbstractValidator<EditMessageCommand>
{
    public EditMessageCommandValidator()
    {
        RuleFor(x => x.Content)
            .NotNull()
            .MaximumLength(SendMessageCommandValidator.MaxContentLength);
    }
}
