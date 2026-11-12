namespace ChatNET.API.Conversations.KeyEpochs;

// The "code" a refused epoch carries in its problem details. Each says why the epoch was refused; the first two mean the browser
// should look again and build the epoch anew.
public static class KeyEpochCodes
{
    // Someone else stored an epoch first, so the one offered is not the next one.
    public const string Stale = "epoch-stale";

    // The people in the conversation, or the generation of a member's key, are not what the epoch was made for.
    public const string MembersChanged = "epoch-members-changed";

    // A member has not set up encryption, so no key can be sealed for them.
    public const string MemberHasNoKeys = "member-has-no-keys";

    // A message was sealed under a key period that a removal or a leave marked for replacement: the person who left still holds that
    // key, so nothing more is said under it. The browser makes the replacement and seals the message again.
    public const string Due = "epoch-due";

    // The keys of the conversation were replaced recently and nothing asks for them to be replaced again. Looking again does not help:
    // a change of members, or time, is what makes the next replacement allowed.
    public const string TooSoon = "epoch-too-soon";
}
