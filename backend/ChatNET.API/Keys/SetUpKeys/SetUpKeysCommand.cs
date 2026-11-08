using ChatNET.API.Keys.Models;
using MediatR;

namespace ChatNET.API.Keys.SetUpKeys;

// The public bundle of generation 1 and the first backup, as the browser made them, base64url. Sent together so a person
// never has keys on the server without the backup that can restore them.
public record SetUpKeysCommand(Guid UserId, string? Bundle, string? Backup) : IRequest<KeyStatusDto>;
