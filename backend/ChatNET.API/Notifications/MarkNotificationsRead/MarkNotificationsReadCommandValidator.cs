using FluentValidation;

namespace ChatNET.API.Notifications.MarkNotificationsRead;

public class MarkNotificationsReadCommandValidator : AbstractValidator<MarkNotificationsReadCommand>
{
    // A page is 30, so this is room for a page at a time; marking everything is done by leaving the ids out.
    public const int MaxIds = 100;

    public MarkNotificationsReadCommandValidator()
    {
        RuleFor(x => x.Ids)
            .Must(ids => ids is null || ids.Count <= MaxIds)
            .WithMessage($"At most {MaxIds} notifications can be named at once.");

        RuleForEach(x => x.Ids)
            .NotEqual(Guid.Empty);
    }
}
