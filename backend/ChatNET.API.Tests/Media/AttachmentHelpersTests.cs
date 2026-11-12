using ChatNET.API.Media;

namespace ChatNET.API.Tests.Media;

// The small rules that decide what is allowed near the bucket: whose key it is. Plain functions, so they are tested without the host.
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
}
