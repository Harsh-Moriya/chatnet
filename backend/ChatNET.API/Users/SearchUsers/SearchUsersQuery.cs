using MediatR;

namespace ChatNET.API.Users.SearchUsers;

public record SearchUsersQuery(Guid UserId, string Term) : IRequest<List<UserSummaryDto>>;

public record UserSummaryDto(Guid Id, string UserName, string DisplayName, string? AvatarUrl);
