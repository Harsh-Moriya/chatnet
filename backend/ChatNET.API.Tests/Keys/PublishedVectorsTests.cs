using System.Buffers.Binary;
using System.Text.Json;
using ChatNET.API.Keys;
using ChatNET.API.Media;
using ChatNET.API.Messaging;

namespace ChatNET.API.Tests.Keys;

// The published test vectors are the bytes a client produces for the formats in the public specification. The server checks the shape of
// what it stores (it cannot open any of it), and those checks are written by hand from the same specification, so this is where they meet:
// every blob a conforming client makes must be one the server accepts, or the constants here have drifted from the specification and a
// second client built from it would be refused.
public class PublishedVectorsTests
{
    private static readonly JsonElement Vectors = Load();

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "docs", "architecture", "encryption-test-vectors.json");
            if (File.Exists(path))
                return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

            directory = directory.Parent;
        }

        throw new FileNotFoundException("docs/architecture/encryption-test-vectors.json was not found above the test output.");
    }

    private static byte[] Hex(JsonElement element, string name) => Convert.FromHexString(element.GetProperty(name).GetString()!);

    [Fact]
    public void The_vectors_are_the_version_the_server_is_written_for()
    {
        Assert.Equal("chatnet-e2ee-test-vectors", Vectors.GetProperty("format").GetString());
        Assert.Equal(1, Vectors.GetProperty("version").GetInt32());
        Assert.Equal(KeyFormats.Version, (byte)Vectors.GetProperty("version").GetInt32());
    }

    [Fact]
    public void A_bundle_is_the_size_and_version_the_server_expects_and_carries_its_generation_where_the_server_reads_it()
    {
        var bundle = Vectors.GetProperty("bundle");
        var wire = Hex(bundle, "wire");

        Assert.Equal(KeyFormats.BundleBytes, wire.Length);
        Assert.True(KeyFormats.IsBundle(wire, out var generation));
        Assert.Equal(bundle.GetProperty("generation").GetInt32(), generation);
    }

    [Fact]
    public void A_backup_is_accepted_with_the_default_limit_and_names_the_key_derivation_the_server_recognises()
    {
        var blob = Hex(Vectors.GetProperty("backup"), "blob");

        Assert.True(KeyFormats.IsBackup(blob, new EncryptionOptions().MaxBackupBytes));
        Assert.Equal(KeyFormats.Pbkdf2Sha256, blob[2]);
    }

    [Fact]
    public void An_envelope_is_the_size_and_version_the_server_expects()
    {
        var envelope = Hex(Vectors.GetProperty("epoch").GetProperty("envelope"), "sealed");

        Assert.Equal(KeyFormats.EnvelopeBytes, envelope.Length);
        Assert.True(KeyFormats.IsEnvelope(envelope));
    }

    [Fact]
    public void Every_message_is_well_formed_and_is_stored_under_the_epoch_it_names()
    {
        var messages = Vectors.GetProperty("messages").EnumerateArray().ToList();
        Assert.NotEmpty(messages);

        foreach (var message in messages)
        {
            var blob = Hex(message, "blob");

            Assert.True(MessageBlob.IsWellFormed(blob));
            Assert.Equal(message.GetProperty("epoch").GetInt32(), MessageBlob.EpochOf(blob));
            Assert.Equal(blob.Length, MessageBlob.Decode(KeyFormats.Encode(blob))?.Length);
        }
    }

    [Fact]
    public void Every_stored_file_is_at_least_the_smallest_the_server_accepts_and_has_the_length_the_specification_gives()
    {
        var files = Vectors.GetProperty("files").EnumerateArray().ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var stored = Hex(file, "stored");
            var plainLength = Hex(file, "plain").Length;
            var chunkSize = file.GetProperty("chunkSize").GetInt32();
            var chunks = Math.Max(1, (plainLength + chunkSize - 1) / chunkSize);

            Assert.True(stored.Length >= SealedFile.MinBytes);
            Assert.Equal(13 + plainLength + chunks * 16, stored.Length);
            Assert.Equal(chunkSize, (int)BinaryPrimitives.ReadUInt32BigEndian(stored.AsSpan(2, 4)));
        }
    }

    [Fact]
    public void A_file_of_the_largest_size_becomes_an_object_the_size_the_specification_gives_at_the_usual_chunk_size()
    {
        const long tenMebibytes = 10 * 1024 * 1024;
        const int usualChunk = 64 * 1024;

        var chunks = (tenMebibytes + usualChunk - 1) / usualChunk;

        Assert.Equal(13 + tenMebibytes + chunks * 16, SealedFile.MaxBytes(tenMebibytes));
    }
}
