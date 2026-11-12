using System.Buffers.Binary;
using ChatNET.API.Keys;
using ChatNET.API.Messaging;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Messaging;

// What the server checks of a message it cannot read: the shape the formats give it, and nothing of what is inside.
public class MessageBlobTests
{
    private static byte[] WithEpoch(uint epoch)
    {
        var blob = FakeMessage.Blob(1, "x");
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(2, 4), epoch);
        return blob;
    }

    // ##### Size #####

    [Fact]
    public void The_smallest_message_is_98_bytes_and_is_accepted()
    {
        var blob = FakeMessage.Blob(1, "");

        Assert.Equal(98, blob.Length);
        Assert.Equal(98, MessageBlob.MinBytes);
        Assert.True(MessageBlob.IsWellFormed(blob));
    }

    [Fact]
    public void A_blob_one_byte_shorter_is_not_a_message()
    {
        Assert.False(MessageBlob.IsWellFormed(FakeMessage.Blob(1, "")[..97]));
    }

    [Fact]
    public void The_largest_message_is_32768_bytes_and_one_more_is_refused()
    {
        var largest = FakeMessage.Blob(1, new string('x', MessageBlob.MaxBytes - MessageBlob.MinBytes));
        var tooLarge = FakeMessage.Blob(1, new string('x', MessageBlob.MaxBytes - MessageBlob.MinBytes + 1));

        Assert.Equal(32768, largest.Length);
        Assert.Equal(32768, MessageBlob.MaxBytes);
        Assert.True(MessageBlob.IsWellFormed(largest));
        Assert.False(MessageBlob.IsWellFormed(tooLarge));
    }

    // ##### Header #####

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(255, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(1, 255)]
    public void A_version_or_suite_the_formats_do_not_have_is_refused(int version, int suite)
    {
        var blob = FakeMessage.Blob(1, "x");
        blob[0] = (byte)version;
        blob[1] = (byte)suite;

        Assert.False(MessageBlob.IsWellFormed(blob));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(1000u)]
    [InlineData((uint)int.MaxValue)]
    public void An_epoch_of_a_conversation_is_accepted(uint epoch)
    {
        Assert.True(MessageBlob.IsWellFormed(WithEpoch(epoch)));
        Assert.Equal((int)epoch, MessageBlob.EpochOf(WithEpoch(epoch)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData((uint)int.MaxValue + 1)]
    [InlineData(uint.MaxValue)]
    public void No_epoch_or_one_that_no_conversation_can_reach_is_refused(uint epoch)
    {
        Assert.False(MessageBlob.IsWellFormed(WithEpoch(epoch)));
        Assert.Equal(0, MessageBlob.EpochOf(WithEpoch(epoch)));
    }

    [Fact]
    public void The_epoch_is_read_as_four_bytes_most_significant_first()
    {
        var blob = FakeMessage.Blob(1, "x");
        blob[2] = 0;
        blob[3] = 0;
        blob[4] = 1;
        blob[5] = 2;

        Assert.Equal(258, MessageBlob.EpochOf(blob));
    }

    // ##### Reading a request #####

    [Fact]
    public void A_blob_in_a_request_is_decoded_to_the_bytes_that_were_sent()
    {
        var blob = FakeMessage.Blob(3, "hello");

        Assert.Equal(blob, MessageBlob.Decode(KeyFormats.Encode(blob)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AAAA=")]
    [InlineData("AA+A")]
    public void Something_that_is_not_base64url_is_not_decoded(string? value)
    {
        Assert.Null(MessageBlob.Decode(value));
    }

    [Fact]
    public void Something_that_is_base64url_but_not_a_message_is_not_decoded()
    {
        Assert.Null(MessageBlob.Decode(KeyFormats.Encode(new byte[200])));
        Assert.Null(MessageBlob.Decode(KeyFormats.Encode(FakeMessage.Blob(1, "")[..97])));
    }

    // A value that would decode to more than the largest message is refused by its length, before anything is decoded.
    [Fact]
    public void A_value_longer_than_the_largest_message_is_refused_without_being_decoded()
    {
        Assert.Null(MessageBlob.Decode(new string('A', 50_000)));
    }
}
