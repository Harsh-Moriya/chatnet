namespace ChatNET.API.Keys;

// The "code" a refused key change carries in its problem details, so the browser knows what to look at again.
public static class KeyCodes
{
    // The bundle offered is not the next generation: another device stored one first, or this one went by what was true earlier.
    // The browser reads its keys again from the server.
    public const string GenerationStale = "generation-stale";

    // The backup was replaced from another device since the version this change was made from. The browser reads it again.
    public const string BackupVersionStale = "backup-version-stale";
}
