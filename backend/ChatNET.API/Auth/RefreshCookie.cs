using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;

namespace ChatNET.API.Auth;

// The refresh token's cookie, in one place so every endpoint sets and removes it the same way.
//
// - HttpOnly: scripts on the page cannot read it, so a cross-site scripting bug cannot steal the
//   long-lived token. That is the reason it is a cookie and not something the page stores.
// - Secure: sent over https only (browsers also accept it for localhost during development).
// - SameSite=Strict: not sent on any request that starts on another site, which closes cross-site
//   request forgery against the two endpoints that use it.
// - Path=/api/auth: sent to the refresh and sign-out endpoints and to nothing else, so the token does
//   not travel with every message.
public static class RefreshCookie
{
    public const string Name = "chatnet_refresh";
    public const string CookiePath = "/api/auth";

    // The refresh and sign-out endpoints refuse a request without this header. A browser sends a custom
    // header on a cross-origin request only after the server has agreed to it, so a page on another
    // site cannot make the call at all. A second layer behind SameSite.
    public const string RequiredHeader = "X-Requested-With";
    public const string RequiredHeaderValue = "ChatNET";

    public static string? Read(HttpRequest request)
        => request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    public static void Write(HttpResponse response, IssuedRefreshToken token)
        => response.Cookies.Append(Name, token.Value, Options(token.ExpiresAt));

    public static void Clear(HttpResponse response)
        => response.Cookies.Delete(Name, Options(expires: null));

    public static void RequireClientHeader(HttpRequest request)
    {
        if (request.Headers[RequiredHeader] != RequiredHeaderValue)
            throw new ForbiddenException($"The {RequiredHeader} header is required.");
    }

    private static CookieOptions Options(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires is null ? null : new DateTimeOffset(DateTime.SpecifyKind(expires.Value, DateTimeKind.Utc)),
    };
}
