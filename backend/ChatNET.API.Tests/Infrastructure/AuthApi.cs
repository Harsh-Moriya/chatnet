using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatNET.API.Auth;
using ChatNET.API.Auth.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace ChatNET.API.Tests.Infrastructure;

// Thin wrappers over the sign-in, refresh and sign-out endpoints. The refresh token lives in a cookie,
// and the cookie is Secure while the test server speaks plain http, so a client's own cookie handling
// would never send it back. These tests pass the cookie by hand instead, which also lets one test act as
// two browsers or as a thief holding a copy.
public static class AuthApi
{
    // A client that neither stores nor sends cookies by itself.
    public static HttpClient CreateCookielessClient(this WebApplicationFactory<Program> host)
        => host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, TestUser user)
        => client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = user.Password });

    public static Task<HttpResponseMessage> RefreshAsync(this HttpClient client, string? cookie, bool withHeader = true)
        => client.SendSessionRequestAsync("/api/auth/refresh", cookie, withHeader);

    public static Task<HttpResponseMessage> LogoutAsync(this HttpClient client, string? cookie, bool withHeader = true)
        => client.SendSessionRequestAsync("/api/auth/logout", cookie, withHeader);

    // Signs in and returns the refresh token that came back in the cookie.
    public static async Task<string> SignInAsync(this HttpClient client, TestUser user)
    {
        var response = await client.LoginAsync(user);
        response.EnsureSuccessStatusCode();

        return response.GetRefreshCookie()?.Value.ToString()
               ?? throw new InvalidOperationException("Sign-in did not set the refresh cookie.");
    }

    // The refresh cookie a response sets, with its attributes, or null if it sets none.
    public static SetCookieHeaderValue? GetRefreshCookie(this HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
            return null;

        return headers
            .Select(header => SetCookieHeaderValue.Parse(header))
            .FirstOrDefault(cookie => cookie.Name == RefreshCookie.Name);
    }

    // True when the response tells the browser to delete the cookie: empty value, expiry in the past.
    public static bool DeletesRefreshCookie(this HttpResponseMessage response)
    {
        var cookie = response.GetRefreshCookie();

        return cookie is not null
               && string.IsNullOrEmpty(cookie.Value.ToString())
               && cookie.Expires is { } expires
               && expires < DateTimeOffset.UtcNow;
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    // What the database should hold for a token: its SHA-256, in lowercase hex.
    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static Task<HttpResponseMessage> SendSessionRequestAsync(
        this HttpClient client, string path, string? cookie, bool withHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (cookie is not null)
            request.Headers.Add("Cookie", $"{RefreshCookie.Name}={cookie}");

        if (withHeader)
        {
            request.Headers.Add(
                RefreshCookie.RequiredHeader,
                RefreshCookie.RequiredHeaderValue);
        }

        return client.SendAsync(request);
    }
}

public static class SessionRows
{
    // Every stored token of the session a refresh token belongs to, oldest first. A test user already has
    // one session from registering, so assertions about a sign-in look at that session's family only.
    public static async Task<List<RefreshToken>> SessionRowsAsync(this ChatApiFactory factory, string token)
    {
        var hash = AuthApi.HashOf(token);

        return await factory.QueryDbAsync(async db =>
        {
            var familyId = (await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash)).FamilyId;

            return await db.RefreshTokens.Where(t => t.FamilyId == familyId).OrderBy(t => t.CreatedAt).ToListAsync();
        });
    }
}
