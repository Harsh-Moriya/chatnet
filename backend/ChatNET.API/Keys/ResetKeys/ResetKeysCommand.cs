using ChatNET.API.Keys.Models;
using MediatR;

namespace ChatNET.API.Keys.ResetKeys;

// New keys for a person who can no longer open the old ones: the next generation, made from a new identity key and account key, and
// the backup that holds them under a new passphrase, base64url. The password of the account is asked again, because this throws away
// the person's access to everything sealed so far and makes everyone who knows them accept a new identity.
public record ResetKeysCommand(Guid UserId, string? Bundle, string? Backup, string? Password) : IRequest<KeyStatusDto>;
