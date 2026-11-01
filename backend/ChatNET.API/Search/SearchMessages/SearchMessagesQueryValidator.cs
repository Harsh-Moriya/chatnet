using FluentValidation;

namespace ChatNET.API.Search.SearchMessages;

public class SearchMessagesQueryValidator : AbstractValidator<SearchMessagesQuery>
{
    public SearchMessagesQueryValidator()
    {
        RuleFor(x => x.Q)
            .NotNull()
            .WithMessage("Say what to search for.");

        // Counted after trimming, as the search reads it. One letter would match most of what was ever said, and a longer
        // text than this is not a search. A missing text is refused by the rule above.
        RuleFor(x => x.Q)
            .Must(q => q is null || q.Trim().Length is >= SearchQueryText.MinLength and <= SearchQueryText.MaxLength)
            .WithMessage($"Search for {SearchQueryText.MinLength} to {SearchQueryText.MaxLength} characters.");
    }
}
