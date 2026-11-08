using ChatNET.API.Devices.Models;

namespace ChatNET.API.Devices.Persistence;

public interface ILinkRequestRepository
{
    // Opens a request unless the person already has "maxPending" that are waiting for approval. Counting and inserting are one
    // transaction behind a lock on the person. The person's requests that expired long ago are removed in the same transaction, so
    // a person never has more than the few they could have opened lately. Returns false at the limit or when the code is already
    // taken by one of the person's requests.
    Task<bool> TryAddAsync(DeviceLinkRequest request, int maxPending, DateTime now, CancellationToken cancellationToken);

    // The request of the person with this code that can still be approved, or null.
    Task<DeviceLinkRequest?> FindOpenByCodeAsync(Guid userId, string code, DateTime now, CancellationToken cancellationToken);

    // The request, if it is the person's own.
    Task<DeviceLinkRequest?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    // Sets the sealed secrets on a request that is still waiting. Conditional on that, so of two devices approving together one
    // wins and the other is told it lost. Returns whether this call approved it.
    Task<bool> TryApproveAsync(Guid userId, Guid id, byte[] payload, DateTime now, CancellationToken cancellationToken);

    // Hands over the sealed secrets of an approved request once and clears them. Null when there is nothing to hand over: not
    // approved yet, already collected, or expired. One statement, so of two collectors only one gets the bytes.
    Task<byte[]?> TryCollectAsync(Guid userId, Guid id, DateTime now, CancellationToken cancellationToken);
}
