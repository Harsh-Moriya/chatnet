namespace ChatNET.API.Conversations.KeyEpochs;

// A new key period as the browser sends it, base64url as everywhere in the API. Every value can be missing here so that a missing
// one is a validation error with a message and not a failure to read the request.
public record EpochRequest(int? Epoch, string? Commitment, string? Signature, List<EnvelopeRequest>? Envelopes);

public record EnvelopeRequest(Guid? UserId, int? Generation, string? Envelope);
