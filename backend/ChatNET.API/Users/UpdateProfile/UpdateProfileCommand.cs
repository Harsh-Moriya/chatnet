using ChatNET.API.Users.SearchUsers;
using MediatR;

namespace ChatNET.API.Users.UpdateProfile;

// The caller changes their own display name. UserId comes from the token. DisplayName is null only when the request left it
// out, which the validator refuses.
public record UpdateProfileCommand(Guid UserId, string? DisplayName) : IRequest<UserSummaryDto>;
