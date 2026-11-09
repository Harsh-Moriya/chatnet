using ChatNET.API.Common.Exceptions;

namespace ChatNET.API.Conversations.KeyEpochs;

// What the server requires of a new epoch before it stores one. It cannot open an envelope or check a signature, so what it can
// do is make sure the epoch is complete: the next number, and an envelope for exactly the people who are in the conversation, each
// sealed to the key they have now. That is enough to stop a member from being left out (and so unable to read) and a person who
// left from being given the key; the members check the rest themselves.
public static class EpochRules
{
    // Throws a conflict that says which part is out of date, so the browser knows what to look at again.
    public static void Check(
        int currentEpoch,
        IReadOnlyCollection<Guid> activeMemberIds,
        IReadOnlyDictionary<Guid, int> currentGenerations,
        EpochSubmission submission)
    {
        if (submission.Epoch != currentEpoch + 1)
            throw new ConflictException(
                "Another epoch was stored first. Look at the conversation again.", KeyEpochCodes.Stale);

        if (activeMemberIds.Any(id => !currentGenerations.ContainsKey(id)))
            throw new ConflictException(
                "Someone in this conversation has not set up encryption yet.", KeyEpochCodes.MemberHasNoKeys);

        // As many envelopes as members, and every member among them: so one envelope each and none for anyone else (the members
        // are distinct, so a person named twice would leave another out). Once that holds, every envelope is for a member whose key is
        // known.
        var sealedFor = submission.UserIds;
        var sameSet = sealedFor.Count == activeMemberIds.Count && activeMemberIds.All(sealedFor.Contains);

        if (!sameSet || submission.Envelopes.Any(e => e.Generation != currentGenerations[e.UserId]))
            throw new ConflictException(
                "The people in this conversation, or their keys, are not the ones this epoch was made for.",
                KeyEpochCodes.MembersChanged);
    }
}
