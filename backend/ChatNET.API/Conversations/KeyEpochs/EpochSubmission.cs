using ChatNET.API.Conversations.Models;
using ChatNET.API.Keys;

namespace ChatNET.API.Conversations.KeyEpochs;

public record EnvelopeSubmission(Guid UserId, int Generation, byte[] Envelope);

// An epoch request that passed validation, decoded.
public record EpochSubmission(int Epoch, byte[] Commitment, byte[] Signature, IReadOnlyList<EnvelopeSubmission> Envelopes)
{
    // Only for a request EpochRequestValidator accepted: it has checked that everything is present and the right size.
    public static EpochSubmission From(EpochRequest request) => new(
        request.Epoch!.Value,
        KeyFormats.Decode(request.Commitment, KeyFormats.CommitmentBytes)!,
        KeyFormats.Decode(request.Signature, KeyFormats.SignatureBytes)!,
        request.Envelopes!
            .Select(e => new EnvelopeSubmission(e.UserId!.Value, e.Generation!.Value, KeyFormats.Decode(e.Envelope, KeyFormats.EnvelopeBytes)!))
            .ToList());

    public IReadOnlyList<Guid> UserIds => Envelopes.Select(e => e.UserId).ToList();

    // The rows to store: the epoch, with an envelope for each member it names.
    public ConversationKeyEpoch ToEntity(Guid conversationId, Guid creatorId, KeyEpochReason reason, DateTime now) => new()
    {
        ConversationId = conversationId,
        Epoch = Epoch,
        CreatedById = creatorId,
        Reason = reason,
        CreatedAt = now,
        KeyCommitment = Commitment,
        Signature = Signature,
        Envelopes = Envelopes
            .Select(e => new ConversationKeyEnvelope
            {
                ConversationId = conversationId,
                Epoch = Epoch,
                UserId = e.UserId,
                AccountKeyGeneration = e.Generation,
                Envelope = e.Envelope,
            })
            .ToList(),
    };
}
