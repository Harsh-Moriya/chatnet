using FluentValidation;

namespace ChatNET.API.Messaging.SendMessage;

public class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    public const int MaxContentLength = 4000;

    public SendMessageCommandValidator()
    {
        // NotEmpty also rejects a string of only whitespace, so a blank message cannot be sent.
        // Length counts UTF-16 code units, which is how .NET and JavaScript both count a string.
        RuleFor(x => x.Content)
            .NotEmpty()
            .MaximumLength(MaxContentLength);

        RuleFor(x => x.ClientMessageId)
            .NotEqual(Guid.Empty)
            .When(x => x.ClientMessageId.HasValue);
    }
}
