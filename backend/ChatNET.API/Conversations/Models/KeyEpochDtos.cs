namespace ChatNET.API.Conversations.Models;

// Bytes travel as base64url without padding, as everywhere else in the API.

// A member of an epoch and the generation of the account key their envelope is sealed to.
public record EpochMemberDto(Guid UserId, int Generation);

// One epoch as one member sees it: the proof that makes it checkable (the commitment, the signature and the list of people it was
// made for) and that member's own envelope, with the generation of their key that opens it. Never another member's envelope.
public record KeyEpochDto(
    int Epoch,
    Guid CreatorId,
    KeyEpochReason Reason,
    DateTime CreatedAt,
    string Commitment,
    string Signature,
    List<EpochMemberDto> Members,
    int Generation,
    string Envelope);

public record KeyEpochPageDto(List<KeyEpochDto> Epochs, bool HasMore);

// What a conversation's members hold about its keys, pushed when it changes: the newest epoch, whether it must be replaced and when it
// was made (null while there is none), so a browser can replace it when it is a week old.
public record ConversationKeyStateDto(Guid ConversationId, int KeyEpoch, bool KeyRotationRequired, DateTime? KeyEpochAt);
