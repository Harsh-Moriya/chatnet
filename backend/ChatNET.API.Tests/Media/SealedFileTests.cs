using ChatNET.API.Media;

namespace ChatNET.API.Tests.Media;

// What the server limits about a file it cannot read: the size of what was stored. The limit people see is on the file before it is
// encrypted, so the largest object is what such a file becomes.
public class SealedFileTests
{
    private const long TenMebibytes = 10 * 1024 * 1024;

    [Fact]
    public void The_smallest_sealed_file_is_a_header_and_the_tag_of_one_empty_chunk()
    {
        Assert.Equal(13 + 16, SealedFile.MinBytes);
    }

    // 160 chunks of 64 KiB, each with a 16 byte tag, and a 13 byte header.
    [Fact]
    public void A_file_of_the_limit_becomes_the_file_the_header_and_a_tag_for_every_chunk()
    {
        Assert.Equal(TenMebibytes + 13 + 160 * 16, SealedFile.MaxBytes(TenMebibytes));
    }

    [Fact]
    public void A_last_chunk_that_is_not_full_still_has_its_tag()
    {
        // 64 KiB and one byte are two chunks.
        Assert.Equal(65537 + 13 + 2 * 16, SealedFile.MaxBytes(65537));
        Assert.Equal(65536 + 13 + 1 * 16, SealedFile.MaxBytes(65536));
    }

    [Fact]
    public void The_stored_content_type_is_one_that_says_nothing_about_the_file()
    {
        Assert.Equal("application/octet-stream", SealedFile.ContentType);
    }
}
