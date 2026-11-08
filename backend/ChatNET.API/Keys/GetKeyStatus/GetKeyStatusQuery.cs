using ChatNET.API.Keys.Models;
using MediatR;

namespace ChatNET.API.Keys.GetKeyStatus;

public record GetKeyStatusQuery(Guid UserId) : IRequest<KeyStatusDto>;
