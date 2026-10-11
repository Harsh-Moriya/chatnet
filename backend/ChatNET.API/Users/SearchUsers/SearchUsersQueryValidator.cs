using FluentValidation;

namespace ChatNET.API.Users.SearchUsers;

public class SearchUsersQueryValidator : AbstractValidator<SearchUsersQuery>
{
    public SearchUsersQueryValidator()
    {
        RuleFor(x => x.Term)
            .NotEmpty()
            .MinimumLength(2)
            .MaximumLength(50);
    }
}
