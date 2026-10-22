using System.Globalization;
using System.Text;

namespace ChatNET.API.Media;

public static class AttachmentNames
{
    public const int MaxLength = 255;

    // What a file is called once it is attached. The name comes from the sender's computer and is shown to
    // other people and written into a download header, so it is made harmless first:
    //   - only the last part of a path is kept ("C:\docs\plan.pdf" is "plan.pdf")
    //   - control characters are removed, and so are the invisible formatting characters (the right-to-left
    //     override among them), which are how "photo.exe" is made to look like "photo.gpj"
    //   - nothing left means "file"
    public static string Clean(string name)
    {
        var lastPart = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];

        var kept = new StringBuilder(lastPart.Length);
        foreach (var character in lastPart)
        {
            if (char.IsControl(character)) continue;
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format) continue;

            kept.Append(character);
        }

        var cleaned = kept.ToString().Trim();
        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength];

        return cleaned.Length == 0 ? "file" : cleaned;
    }
}
