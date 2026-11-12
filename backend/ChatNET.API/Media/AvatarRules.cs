namespace ChatNET.API.Media;

// What a picture may be. Fixed in code and not configurable: the browser shrinks a picture to a few tens of kilobytes
// before sending it, and the server only has to refuse what is clearly not one. SVG is not allowed, because it can carry
// script and is served from the same address as the page.
public static class AvatarRules
{
    public const long MaxBytes = 2 * 1024 * 1024;

    private static readonly string[] AllowedTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    // The allowed type this one matches (ignoring case and parameters), or null. "Image/PNG; charset=x" is "image/png", so an uppercase
    // letter cannot get a picture past the list.
    public static string? Match(string contentType)
    {
        var parameters = contentType.IndexOf(';');
        var normalized = (parameters < 0 ? contentType : contentType[..parameters]).Trim().ToLowerInvariant();

        return AllowedTypes.FirstOrDefault(type => type == normalized);
    }
}
