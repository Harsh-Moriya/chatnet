using ChatNET.API.Keys.Models;
using MediatR;

namespace ChatNET.API.Keys.ReplaceKeyBackup;

// A new backup of the same secrets under a new passphrase, made from the version the device last read.
public record ReplaceKeyBackupCommand(Guid UserId, string? Blob, int? ExpectedVersion) : IRequest<BackupInfoDto>;
