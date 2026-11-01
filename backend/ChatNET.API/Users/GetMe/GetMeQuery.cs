using ChatNET.API.Users.SearchUsers;
using MediatR;

namespace ChatNET.API.Users.GetMe;

// The caller's own profile, as everyone else sees it.
public record GetMeQuery(Guid UserId) : IRequest<UserSummaryDto>;
