using ChatNET.API.Keys.Models;
using MediatR;

namespace ChatNET.API.Keys.GetKeyBackup;

public record GetKeyBackupQuery(Guid UserId) : IRequest<KeyBackupDto>;
