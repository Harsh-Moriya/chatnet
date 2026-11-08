namespace ChatNET.API.Conversations.Models;

// Why a conversation got a new key. Created is the first key of a conversation, MemberAdded the one made when people
// join, Rotated any other (after someone left, or because a member chose to).
public enum KeyEpochReason
{
    Created,
    MemberAdded,
    Rotated,
}

// One key period of a conversation. The key itself is never here: it is made in a member's browser and reaches each member
// sealed in their envelope. What the server keeps is the proof that makes the epoch checkable by the members: the commitment to
// the key and the creator's signature over who received it. The server cannot verify the signature and does not need to.
//
// The members of an epoch are the people who have an envelope for it, so there is no separate member list to keep in step.
public class ConversationKeyEpoch
{
    public Guid ConversationId { get; set; }

    // 1, 2, 3 and so on. Conversation.KeyEpoch is the highest.
    public int Epoch { get; set; }

    public Guid CreatedById { get; set; }

    public KeyEpochReason Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    // 32 bytes: HKDF of the key bound to the conversation and the epoch.
    public byte[] KeyCommitment { get; set; } = [];

    // 64 bytes: the creator's Ed25519 signature over the epoch record.
    public byte[] Signature { get; set; } = [];

    public ICollection<ConversationKeyEnvelope> Envelopes { get; set; } = [];
}

// The key of one epoch sealed for one member, to the generation of their account key named here. Only that member can open it.
public class ConversationKeyEnvelope
{
    public Guid ConversationId { get; set; }

    public int Epoch { get; set; }

    public Guid UserId { get; set; }

    public int AccountKeyGeneration { get; set; }

    // 94 bytes (see KeyFormats.EnvelopeBytes).
    public byte[] Envelope { get; set; } = [];
}
