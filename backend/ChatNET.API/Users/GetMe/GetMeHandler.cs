using ChatNET.API.Common.Exceptions;
using ChatNET.API.Users.Persistence;
using ChatNET.API.Users.SearchUsers;
using MediatR;

namespace ChatNET.API.Users.GetMe;

public class GetMeHandler : IRequestHandler<GetMeQuery, UserSummaryDto>
{
    private readonly IProfileRepository _profiles;

    public GetMeHandler(IProfileRepository profiles)
    {
        _profiles = profiles;
    }

    // A token outlives the account it was issued for, so an id that is no longer a user is a 404 and not an empty profile.
    public async Task<UserSummaryDto> Handle(GetMeQuery request, CancellationToken cancellationToken)
        => await _profiles.GetSummaryAsync(request.UserId, cancellationToken)
           ?? throw new NotFoundException("User", request.UserId);
}
