namespace ChatNET.API.Devices.Models;

public record DeviceDto(Guid Id, DeviceKind Kind, string Name, DateTime CreatedAt, DateTime LastActiveAt);

// Answer to opening a request to link a device. The code is the one the new device already shows; it is not returned.
public record LinkRequestCreatedDto(Guid Id, DateTime ExpiresAt);

// What the signed-in device is shown to approve: which device is asking, and the key it must seal the secrets to.
public record LinkRequestDto(Guid Id, string TemporaryPublicKey, DeviceKind Kind, string Name, DateTime CreatedAt, DateTime ExpiresAt);

public enum LinkRequestState
{
    // Waiting for a signed-in device to approve.
    Pending,

    // Approved: this answer carries the sealed secrets, and the next will say Collected.
    Approved,

    // The secrets were already handed over.
    Collected,

    // Nobody approved in time, or the answer was not collected in time.
    Expired,
}

public record LinkRequestStatusDto(LinkRequestState State, string? Payload, DateTime ExpiresAt);
