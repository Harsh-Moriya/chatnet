using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Media;

public static class AttachmentKinds
{
    // The type without parameters and in lower case: "Image/PNG; charset=x" is "image/png". Content types are
    // compared in this form everywhere, so an uppercase letter cannot get a file past the list of allowed types.
    public static string Normalize(string contentType)
    {
        var parameters = contentType.IndexOf(';');
        var type = parameters < 0 ? contentType : contentType[..parameters];

        return type.Trim().ToLowerInvariant();
    }

    // Images and videos are shown in the message; everything else is a file to download.
    public static AttachmentType TypeOf(string contentType)
    {
        var type = Normalize(contentType);

        if (type.StartsWith("image/", StringComparison.Ordinal)) return AttachmentType.Image;
        if (type.StartsWith("video/", StringComparison.Ordinal)) return AttachmentType.Video;

        return AttachmentType.File;
    }
}
