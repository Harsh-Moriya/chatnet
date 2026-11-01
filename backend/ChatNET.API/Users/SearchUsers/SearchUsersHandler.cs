using ChatNET.API.Auth.Models;
using ChatNET.API.Media;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Users.SearchUsers;

public class SearchUsersHandler : IRequestHandler<SearchUsersQuery, List<UserSummaryDto>>
{
    private const int MaxResults = 20;

    private readonly UserManager<AppUser> _userManager;

    public SearchUsersHandler(UserManager<AppUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<List<UserSummaryDto>> Handle(SearchUsersQuery request, CancellationToken cancellationToken)
    {
        // LIKE treats % and _ as wildcards, so they are escaped to make the term literal.
        var pattern = EscapeLike(request.Term.Trim()) + "%";

        return await _userManager.Users
            .Where(u => u.Id != request.UserId
                        && !u.IsDeactivated
                        && (EF.Functions.ILike(u.UserName!, pattern, "\\")
                            || EF.Functions.ILike(u.DisplayName, pattern, "\\")))
            .OrderBy(u => u.DisplayName)
            .Take(MaxResults)
            .Select(u => new UserSummaryDto(u.Id, u.UserName!, u.DisplayName, AvatarKeys.PublicUrl(u.AvatarKey)))
            .ToListAsync(cancellationToken);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
