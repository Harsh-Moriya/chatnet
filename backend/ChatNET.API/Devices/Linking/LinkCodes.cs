using System.Text.RegularExpressions;

namespace ChatNET.API.Devices.Linking;

// The shape of a link code: ten characters of Crockford base32, which has no I, L, O or U. The server cannot check that a code
// matches a key (it is the device that approves who does), so it only checks that what it stores and looks up is a code.
public static partial class LinkCodes
{
    public static bool IsCode(string? value) => value is not null && CodePattern().IsMatch(value);

    // The length is written out because an attribute needs a constant; a test pins it to DeviceLinkRequestConfiguration.CodeLength.
    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{10}$")]
    private static partial Regex CodePattern();
}
