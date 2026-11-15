using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Calls.Ice;

// One entry of the list a browser or a phone hands to RTCPeerConnection as iceServers. A STUN entry has no credentials, and the properties
// that are not there are left out of the JSON: a null username would reach a browser as the text "null".
public sealed record IceServerDto(
    string[] Urls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Username = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Credential = null);

// Makes the servers a person's call may use, with a credential for the relay made for them. The scheme is the one TURN servers speak with
// use-auth-secret (the TURN REST API): the username is the time the credential stops working and who it is for, and the password is the
// HMAC-SHA1 of the username under the shared secret. The relay computes the same and compares, so it needs no list of people, and it refuses
// a username whose time has passed. SHA-1 is what the relay speaks; the secret never leaves the server and a credential lives an hour.
public sealed class IceServers
{
    private readonly TurnOptions _options;
    private readonly TimeProvider _time;

    public IceServers(IOptions<TurnOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
    }

    // Empty when the relay is not set up: a call then goes between the two browsers or not at all.
    public IReadOnlyList<IceServerDto> For(Guid userId)
    {
        if (!_options.Enabled)
            return [];

        var servers = new List<IceServerDto>();

        if (_options.StunUrls.Count > 0)
            servers.Add(new IceServerDto([.. _options.StunUrls]));

        // The user's id, never their name or address: it is in the relay's log.
        var expires = _time.GetUtcNow().Add(_options.CredentialLifetime).ToUnixTimeSeconds();
        var username = $"{expires}:{userId:D}";

        servers.Add(new IceServerDto([.. _options.Urls], username, Credential(username)));

        return servers;
    }

    private string Credential(string username)
        => Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(_options.Secret), Encoding.UTF8.GetBytes(username)));
}
