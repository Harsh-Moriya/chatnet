using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChatNET.API.Tests.Infrastructure;

// What a test needs to play the browser side of the encryption endpoints. The server cannot open any of these blobs and only
// checks their shape, so the bytes are made to have the right shape and are otherwise random.
public static class EncryptionApi
{
    public const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

    public static byte[] FromB64(string text) => Base64Url.DecodeFromChars(text);

    // A bundle: version, suite, generation, identity public key, account public key, signature (134 bytes).
    public static byte[] Bundle(int generation = 1, byte[]? identity = null, byte version = 1, byte suite = 1, int length = 134)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        bytes[0] = version;
        bytes[1] = suite;
        if (length >= 6)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(2), (uint)generation);
        if (identity is not null)
            identity.CopyTo(bytes, 6);
        return bytes;
    }

    public static byte[] Identity() => RandomNumberGenerator.GetBytes(32);

    // A backup: version, suite, key derivation, then random bytes for the rest of the header and the ciphertext.
    public static byte[] Backup(int length = 120, byte version = 1, byte suite = 1, byte kdf = 1)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        bytes[0] = version;
        bytes[1] = suite;
        bytes[2] = kdf;
        return bytes;
    }

    // A sealed box: version, suite, then random bytes.
    public static byte[] SealedBox(int length = 120, byte version = 1, byte suite = 1)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        bytes[0] = version;
        bytes[1] = suite;
        return bytes;
    }

    public static string NewCode()
    {
        var chars = RandomNumberGenerator.GetItems<char>(CodeAlphabet, 10);
        return new string(chars);
    }

    // ##### Calls #####

    public static Task<HttpResponseMessage> SetUpKeysAsync(this HttpClient client, byte[]? bundle = null, byte[]? backup = null) =>
        client.PostAsJsonAsync("/api/keys/me", new { bundle = B64(bundle ?? Bundle()), backup = B64(backup ?? Backup()) });

    public static Task<HttpResponseMessage> ReplaceBackupAsync(this HttpClient client, byte[] blob, int? expectedVersion) =>
        client.PutAsJsonAsync("/api/keys/me/backup", new { blob = B64(blob), expectedVersion });

    public static Task<HttpResponseMessage> LookupKeysAsync(this HttpClient client, IReadOnlyCollection<Guid> userIds) =>
        client.PostAsJsonAsync("/api/keys/lookup", new { userIds });

    public static Task<HttpResponseMessage> RegisterDeviceAsync(this HttpClient client, string kind = "Web", string name = "Test browser") =>
        client.PostAsJsonAsync("/api/devices", new { kind, name });

    // Registers a device and returns its id.
    public static async Task<Guid> AddDeviceAsync(this HttpClient client, string kind = "Web", string name = "Test browser")
    {
        var response = await client.RegisterDeviceAsync(kind, name);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> OpenLinkAsync(
        this HttpClient client, string? code = null, byte[]? temporaryKey = null, string kind = "Web", string name = "New browser") =>
        client.PostAsJsonAsync("/api/devices/link-requests", new
        {
            temporaryPublicKey = B64(temporaryKey ?? RandomNumberGenerator.GetBytes(32)),
            code = code ?? NewCode(),
            kind,
            name,
        });

    public static async Task<Guid> OpenedLinkIdAsync(this HttpClient client, string? code = null, byte[]? temporaryKey = null)
    {
        var response = await client.OpenLinkAsync(code, temporaryKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> ApproveLinkAsync(this HttpClient client, Guid requestId, byte[]? payload = null) =>
        client.PostAsJsonAsync($"/api/devices/link-requests/{requestId}/approve", new { payload = B64(payload ?? SealedBox()) });

    public static Task<HttpResponseMessage> PollLinkAsync(this HttpClient client, Guid requestId) =>
        client.GetAsync($"/api/devices/link-requests/{requestId}");

    public static Task<HttpResponseMessage> FindLinkAsync(this HttpClient client, string code) =>
        client.GetAsync($"/api/devices/link-requests?code={code}");

    // ##### Arranging #####

    // A person with encryption set up, and a client signed in as them.
    public static async Task<(TestUser User, HttpClient Client)> UserWithKeysAsync(this ChatApiFactory factory)
    {
        var user = await factory.RegisterUserAsync("keys");
        var client = factory.CreateClientFor(user);
        var response = await client.SetUpKeysAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (user, client);
    }
}
