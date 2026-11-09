using ChatNET.API.Common.Exceptions;

namespace ChatNET.API.Conversations.KeyEpochs;

// What the server requires of a new epoch before it stores one. It cannot open an envelope or check a signature, so what it can
// do is make sure the epoch is complete: the next number, and an envelope for exactly the people who are in the conversation, each
// sealed to the key they have now. That is enough to stop a member from being left out (and so unable to read) and a person who
// left from being given the key; the members check the rest themselves.
public static class EpochRules
{
    // How long a key period stands before a member may replace it for no other reason. A change of members asks for a replacement by
    // itself (an add carries the next epoch, a removal or a leave marks the one in force as due), so what this holds back is a member who
    // stores epochs again and again: each one holds an envelope for every member, and every other browser must open it.
    public static readonly TimeSpan MinReplacementInterval = TimeSpan.FromDays(1);

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

    // An epoch stored on its own, with no change of members, is allowed when the conversation has none yet, when the one in force is
    // marked as due for replacement, or when that one is at least MinReplacementInterval old. It runs after Check, so a member who lost
    // a race hears that the epoch is out of date and not that it is too soon.
    public static void CheckReplacement(int currentEpoch, bool replacementDue, DateTime? newestEpochAt, DateTime now)
    {
        if (currentEpoch == 0 || replacementDue)
            return;

        if (newestEpochAt is { } madeAt && now - madeAt < MinReplacementInterval)
            throw new ConflictException(
                "The keys of this conversation were replaced recently and nothing asks for them to be replaced again.",
                KeyEpochCodes.TooSoon);
    }
}
