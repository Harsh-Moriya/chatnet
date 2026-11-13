using ChatNET.API.Keys.Models;
using MediatR;

namespace ChatNET.API.Keys.AddKeyGeneration;

// The next generation of a person's account key, signed by their identity key, and the backup that holds it, as the browser made
// them, base64url. "ExpectedBackupVersion" is the version of the backup the new one was made from. "RemoveDeviceId" is the device that
// was lost, when this is why the keys change: it is removed in the same step, so there is no moment where it is gone but could still
// read what is sealed next, or the keys changed but it is still signed in.
public record AddKeyGenerationCommand(
    Guid UserId, string? Bundle, string? Backup, int? ExpectedBackupVersion, Guid? RemoveDeviceId) : IRequest<KeyStatusDto>;
