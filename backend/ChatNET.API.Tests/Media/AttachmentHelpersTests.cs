using ChatNET.API.Media;
using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Tests.Media;

// The small rules that decide what is allowed near the bucket: whose key it is, what a file is called, and what
// kind of file it is. Plain functions, so they are tested without the host.
public class AttachmentHelpersTests
{
    private static readonly Guid Conversation = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    // ##### Upload keys #####

    [Fact]
    public void A_new_upload_key_is_in_a_folder_made_from_the_conversation_and_the_person()
    {
        var key = AttachmentKeys.NewUploadKey(Conversation, User);

        Assert.StartsWith($"pending/{Conversation}/{User}/", key);
        Assert.True(AttachmentKeys.IsUploadKeyFor(key, Conversation, User));
    }

    [Fact]
    public void Two_upload_keys_are_never_the_same()
    {
        Assert.NotEqual(AttachmentKeys.NewUploadKey(Conversation, User), AttachmentKeys.NewUploadKey(Conversation, User));
    }

    [Fact]
    public void An_upload_key_belongs_to_one_person_in_one_conversation()
    {
        var key = AttachmentKeys.NewUploadKey(Conversation, User);

        Assert.False(AttachmentKeys.IsUploadKeyFor(key, Conversation, Guid.NewGuid()));
        Assert.False(AttachmentKeys.IsUploadKeyFor(key, Guid.NewGuid(), User));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pending/")]
    [InlineData("not a key")]
    [InlineData("../pending")]
    public void A_key_that_is_not_an_upload_key_is_refused(string key)
    {
        Assert.False(AttachmentKeys.IsUploadKeyFor(key, Conversation, User));
    }

    [Fact]
    public void A_stored_attachment_cannot_be_named_as_an_upload()
    {
        var stored = AttachmentKeys.StoredKey(Conversation, Guid.NewGuid());

        Assert.False(AttachmentKeys.IsUploadKeyFor(stored, Conversation, User));
    }

    [Fact]
    public void A_key_with_anything_after_the_upload_id_is_refused()
    {
        var key = AttachmentKeys.NewUploadKey(Conversation, User);

        Assert.False(AttachmentKeys.IsUploadKeyFor(key + "/more", Conversation, User));
        Assert.False(AttachmentKeys.IsUploadKeyFor(key + "../", Conversation, User));
        Assert.False(AttachmentKeys.IsUploadKeyFor(key + " ", Conversation, User));
    }

    [Fact]
    public void A_key_written_in_another_form_is_refused_even_when_it_names_the_same_ids()
    {
        var upload = Guid.NewGuid();
        var prefix = $"pending/{Conversation}/{User}/";

        Assert.True(AttachmentKeys.IsUploadKeyFor(prefix + upload, Conversation, User));
        Assert.False(AttachmentKeys.IsUploadKeyFor(prefix + upload.ToString().ToUpperInvariant(), Conversation, User));
        Assert.False(AttachmentKeys.IsUploadKeyFor(prefix + upload.ToString("N"), Conversation, User));
        Assert.False(AttachmentKeys.IsUploadKeyFor(prefix + "{" + upload + "}", Conversation, User));
    }

    [Fact]
    public void A_stored_key_is_in_the_conversations_folder_under_the_attachment_id()
    {
        var id = Guid.NewGuid();

        Assert.Equal($"attachments/{Conversation}/{id}", AttachmentKeys.StoredKey(Conversation, id));
    }

    // ##### File names #####

    [Theory]
    [InlineData("photo.png", "photo.png")]
    [InlineData("  spaced out.pdf  ", "spaced out.pdf")]
    [InlineData("résumé 履歴書.pdf", "résumé 履歴書.pdf")]
    public void An_ordinary_name_is_kept(string name, string expected)
    {
        Assert.Equal(expected, AttachmentNames.Clean(name));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\docs\\plan.pdf", "plan.pdf")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("../../secret.txt", "secret.txt")]
    [InlineData("a/b\\c/d.txt", "d.txt")]
    public void Only_the_last_part_of_a_path_is_kept(string name, string expected)
    {
        Assert.Equal(expected, AttachmentNames.Clean(name));
    }

    [Fact]
    public void Control_characters_are_removed()
    {
        Assert.Equal("ab.txt", AttachmentNames.Clean("a\r\nb\0.txt"));
        Assert.Equal("ab.txt", AttachmentNames.Clean("a\tb.txt"));
    }

    // The right-to-left override turns "invoice\u202Egpj.exe" into text that reads "invoiceexe.jpg".
    [Fact]
    public void Invisible_formatting_characters_that_disguise_an_extension_are_removed()
    {
        Assert.Equal("invoicegpj.exe", AttachmentNames.Clean("invoice\u202Egpj.exe"));
        Assert.Equal("ab.pdf", AttachmentNames.Clean("a\u200Bb.pdf"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("folder/")]
    [InlineData("\u202E")]
    public void A_name_with_nothing_left_becomes_file(string name)
    {
        Assert.Equal("file", AttachmentNames.Clean(name));
    }

    [Fact]
    public void A_name_is_cut_to_the_longest_allowed()
    {
        var cleaned = AttachmentNames.Clean(new string('a', 400));

        Assert.Equal(AttachmentNames.MaxLength, cleaned.Length);
    }

    // ##### Content types #####

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData("image/png; charset=binary", "image/png")]
    [InlineData("  Application/PDF  ", "application/pdf")]
    public void A_content_type_is_compared_without_case_or_parameters(string given, string expected)
    {
        Assert.Equal(expected, AttachmentKinds.Normalize(given));
    }

    [Theory]
    [InlineData("image/png", AttachmentType.Image)]
    [InlineData("Image/WebP", AttachmentType.Image)]
    [InlineData("video/mp4", AttachmentType.Video)]
    [InlineData("application/pdf", AttachmentType.File)]
    [InlineData("text/plain", AttachmentType.File)]
    [InlineData("application/zip", AttachmentType.File)]
    [InlineData("", AttachmentType.File)]
    public void The_kind_of_a_file_follows_its_content_type(string contentType, AttachmentType expected)
    {
        Assert.Equal(expected, AttachmentKinds.TypeOf(contentType));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/PNG", true)]
    [InlineData("image/png; charset=binary", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/html", false)]
    [InlineData("application/x-msdownload", false)]
    [InlineData("", false)]
    public void Only_the_configured_types_are_allowed(string contentType, bool allowed)
    {
        var options = new StorageOptions { AllowedContentTypes = ["image/png", "application/pdf"] };

        Assert.Equal(allowed, options.Allows(contentType));
    }

    [Fact]
    public void The_configured_list_is_compared_without_case_too()
    {
        var options = new StorageOptions { AllowedContentTypes = ["Image/PNG"] };

        Assert.True(options.Allows("image/png"));
    }
}
