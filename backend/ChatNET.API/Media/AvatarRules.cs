namespace ChatNET.API.Media;

// What a picture may be. Fixed in code and not configurable: the browser shrinks a picture to a few tens of kilobytes
// before sending it, and the server only has to refuse what is clearly not one. SVG is not allowed, because it can carry
// script and is served from the same address as the page.
public static class AvatarRules
{
    public const long MaxBytes = 2 * 1024 * 1024;

    private static readonly string[] AllowedTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    // The allowed type this one matches (ignoring case and parameters), or null.
    public static string? Match(string contentType)
    {
        var normalized = AttachmentKinds.Normalize(contentType);
        return AllowedTypes.FirstOrDefault(type => type == normalized);
    }
}
