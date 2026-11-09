using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatNET.API.Tests.Infrastructure;

// A new key period as a browser sends it. The server cannot open an envelope or check a signature, so the bytes are made to have the
// right shape and are otherwise random. Every part can be changed to make a request wrong in one way.
public sealed class EpochBody
{
    public int? Epoch { get; set; }

    public string? Commitment { get; set; }

    public string? Signature { get; set; }

    public List<EnvelopeBody>? Envelopes { get; set; }

    // An epoch for these people, each sealed to generation 1 of their key.
    public static EpochBody For(int epoch, params Guid[] members) => For(epoch, members.Select(id => (id, 1)));

    public static EpochBody For(int epoch, IEnumerable<(Guid UserId, int Generation)> members) => new()
    {
        Epoch = epoch,
        Commitment = EncryptionApi.B64(RandomNumberGenerator.GetBytes(32)),
        Signature = EncryptionApi.B64(RandomNumberGenerator.GetBytes(64)),
        Envelopes = members.Select(m => new EnvelopeBody { UserId = m.UserId, Generation = m.Generation, Envelope = EncryptionApi.B64(EncryptionApi.Envelope()) }).ToList(),
    };

    // What was generated, for a test to compare with what was stored. Not part of the request.
    [JsonIgnore]
    public byte[] CommitmentBytes => EncryptionApi.FromB64(Commitment!);

    [JsonIgnore]
    public byte[] SignatureBytes => EncryptionApi.FromB64(Signature!);

    public byte[] EnvelopeOf(Guid userId) => EncryptionApi.FromB64(Envelopes!.Single(e => e.UserId == userId).Envelope!);
}

public sealed class EnvelopeBody
{
    public Guid? UserId { get; set; }

    public int? Generation { get; set; }

    public string? Envelope { get; set; }
}

public static class EpochApi
{
    public static Task<HttpResponseMessage> PostEpochAsync(this HttpClient client, Guid conversationId, EpochBody? body) =>
        client.PostAsJsonAsync($"/api/conversations/{conversationId}/key-epochs", body);

    // Stores an epoch and checks it was accepted.
    public static async Task StoreEpochAsync(this HttpClient client, Guid conversationId, EpochBody body)
    {
        var response = await client.PostEpochAsync(conversationId, body);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static Task<HttpResponseMessage> GetEpochsAsync(this HttpClient client, Guid conversationId, int? from = null, int? limit = null)
    {
        var query = new List<string>();
        if (from is not null)
            query.Add($"from={from}");
        if (limit is not null)
            query.Add($"limit={limit}");

        return client.GetAsync($"/api/conversations/{conversationId}/key-epochs" + (query.Count > 0 ? "?" + string.Join("&", query) : ""));
    }

    public static async Task<JsonElement> ReadEpochsAsync(this HttpClient client, Guid conversationId, int? from = null, int? limit = null)
    {
        var response = await client.GetEpochsAsync(conversationId, from, limit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }
}
