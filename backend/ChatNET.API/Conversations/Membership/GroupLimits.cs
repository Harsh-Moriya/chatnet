namespace ChatNET.API.Conversations.Membership;

public static class GroupLimits
{
    // How many people besides the owner a group may be created with, and may grow to hold. Every message, typing event
    // and read position goes to every member, so the cost of a group grows with this number.
    public const int MaxOtherMembers = 100;

    // Everyone who is in a group at one time.
    public const int MaxActiveMembers = MaxOtherMembers + 1;

    public const int MaxNameLength = 100;
}
