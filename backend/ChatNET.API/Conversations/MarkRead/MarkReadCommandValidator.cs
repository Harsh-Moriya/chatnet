using FluentValidation;

namespace ChatNET.API.Conversations.MarkRead;

public class MarkReadCommandValidator : AbstractValidator<MarkReadCommand>
{
    public MarkReadCommandValidator()
    {
        // An empty id is not a message. The handler would refuse it as unknown too; saying so here
        // spares it a query.
        RuleFor(x => x.MessageId).NotEqual(Guid.Empty);
    }
}
