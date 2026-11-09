namespace ChatNET.API.Conversations.KeyEpochs;

// The "code" a refused epoch carries in its problem details. Each means the browser should look again and build the epoch anew,
// and each says what changed.
public static class KeyEpochCodes
{
    // Someone else stored an epoch first, so the one offered is not the next one.
    public const string Stale = "epoch-stale";

    // The people in the conversation, or the generation of a member's key, are not what the epoch was made for.
    public const string MembersChanged = "epoch-members-changed";

    // A member has not set up encryption, so no key can be sealed for them.
    public const string MemberHasNoKeys = "member-has-no-keys";
}
