using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Push;

namespace ChatNET.API.Tests.Push;

// The words of an alert are made by the same rules as in the page (alerts.ts), so an alert reads the same whether it came over
// the connection or as a push. These are the cases of alerts.test.ts that apply, in C#.
public class PushAlertTextTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();

    private static MessageDto Message(string? content, string sender = "Alice") => new(
        Guid.NewGuid(), ConversationId, Guid.NewGuid(), sender, content, DateTime.UtcNow, null, [],
        EditedAt: null, EditCount: 0, IsDeleted: false);

    private static NotificationDto Notification(NotificationType type, string actor = "Alice", string? preview = null, Guid? messageId = null) =>
        new(Guid.NewGuid(), type, ConversationId, Guid.NewGuid(), actor, messageId, preview, false, DateTime.UtcNow);

    // ##### Preview #####

    [Fact]
    public void A_short_text_is_kept_as_it_is_without_the_space_around_it()
    {
        Assert.Equal("see you at 5", PushAlertText.Preview("  see you at 5 \n"));
    }

    [Fact]
    public void A_text_of_exactly_the_limit_is_not_cut()
    {
        var text = new string('a', PushAlertText.PreviewLength);

        Assert.Equal(text, PushAlertText.Preview(text));
    }

    [Fact]
    public void A_longer_text_is_cut_at_the_limit_and_ends_with_an_ellipsis()
    {
        var preview = PushAlertText.Preview(new string('a', PushAlertText.PreviewLength + 1));

        Assert.Equal(new string('a', PushAlertText.PreviewLength) + "…", preview);
    }

    [Fact]
    public void The_space_left_at_the_end_of_a_cut_is_dropped_before_the_ellipsis()
    {
        var text = new string('a', PushAlertText.PreviewLength - 1) + " " + "bbbb";

        Assert.Equal(new string('a', PushAlertText.PreviewLength - 1) + "…", PushAlertText.Preview(text));
    }

    // 😀 is two UTF-16 code units; a cut between them would leave half a character that no screen can show.
    [Fact]
    public void A_cut_never_lands_between_the_two_halves_of_a_character()
    {
        var text = new string('a', PushAlertText.PreviewLength - 1) + "😀" + "tail";

        var preview = PushAlertText.Preview(text);

        Assert.Equal(new string('a', PushAlertText.PreviewLength - 1) + "…", preview);
        Assert.All(preview, c => Assert.False(char.IsSurrogate(c)));
    }

    [Fact]
    public void A_text_that_is_only_space_has_no_preview()
    {
        Assert.Equal(string.Empty, PushAlertText.Preview("   \t "));
    }

    // ##### A message #####

    [Fact]
    public void A_message_in_a_group_is_titled_with_the_sender_and_the_group()
    {
        var alert = PushAlertText.ForMessage(Message("hello", "Alice"), "Weekend plans", fileCount: 0);

        Assert.Equal("Alice in Weekend plans", alert.Title);
        Assert.Equal("hello", alert.Body);
    }

    [Fact]
    public void A_message_in_a_direct_conversation_is_titled_with_the_sender()
    {
        var alert = PushAlertText.ForMessage(Message("hello", "Alice"), groupName: null, fileCount: 0);

        Assert.Equal("Alice", alert.Title);
    }

    [Fact]
    public void A_message_alert_leads_to_the_conversation_and_replaces_the_one_before_it()
    {
        var message = Message("hello");

        var alert = PushAlertText.ForMessage(message, "Weekend plans", fileCount: 0);

        Assert.Equal(PushAlertTypes.Message, alert.Type);
        Assert.Equal(ConversationId, alert.ConversationId);
        Assert.Equal(message.Id, alert.MessageId);
        Assert.Equal($"conversation-{ConversationId}", alert.Tag);
    }

    [Theory]
    [InlineData(1, "Sent a file")]
    [InlineData(2, "Sent 2 files")]
    [InlineData(4, "Sent 4 files")]
    public void A_message_of_only_files_says_how_many(int files, string body)
    {
        Assert.Equal(body, PushAlertText.ForMessage(Message(string.Empty), "Team", files).Body);
        Assert.Equal(body, PushAlertText.ForMessage(Message("   "), "Team", files).Body);
    }

    [Fact]
    public void The_text_of_a_message_comes_before_its_files()
    {
        Assert.Equal("look at this", PushAlertText.ForMessage(Message("look at this"), "Team", fileCount: 3).Body);
    }

    [Fact]
    public void A_message_with_neither_text_nor_files_has_an_empty_body()
    {
        Assert.Equal(string.Empty, PushAlertText.ForMessage(Message(null), "Team", fileCount: 0).Body);
    }

    [Fact]
    public void A_long_message_is_cut_for_the_alert()
    {
        var body = PushAlertText.ForMessage(Message(new string('x', 500)), "Team", fileCount: 0).Body;

        Assert.Equal(PushAlertText.PreviewLength + 1, body.Length);
    }

    // ##### A mention #####

    [Fact]
    public void A_mention_in_a_group_names_who_and_where_and_shows_the_message()
    {
        var messageId = Guid.NewGuid();
        var notification = Notification(NotificationType.Mention, "Alice", "@bob are you coming?", messageId);

        var alert = PushAlertText.ForMention(notification, ConversationId, "Weekend plans");

        Assert.Equal("Alice mentioned you in Weekend plans", alert.Title);
        Assert.Equal("@bob are you coming?", alert.Body);
        Assert.Equal(PushAlertTypes.Mention, alert.Type);
        Assert.Equal(messageId, alert.MessageId);
        Assert.Equal(ConversationId, alert.ConversationId);
        Assert.Equal($"conversation-{ConversationId}", alert.Tag);
    }

    [Fact]
    public void A_mention_in_a_direct_conversation_does_not_name_a_place()
    {
        var alert = PushAlertText.ForMention(Notification(NotificationType.Mention, "Alice"), ConversationId, groupName: null);

        Assert.Equal("Alice mentioned you", alert.Title);
    }

    [Fact]
    public void A_mention_of_a_message_that_is_gone_has_no_body()
    {
        var alert = PushAlertText.ForMention(Notification(NotificationType.Mention, preview: null), ConversationId, "Team");

        Assert.Equal(string.Empty, alert.Body);
    }

    // ##### An invitation #####

    [Fact]
    public void An_invitation_names_who_added_the_person_and_to_which_group()
    {
        var alert = PushAlertText.ForInvitation(Notification(NotificationType.MemberAdded, "Alice"), ConversationId, "Weekend plans");

        Assert.Equal("Alice added you to Weekend plans", alert.Title);
        Assert.Equal(string.Empty, alert.Body);
        Assert.Equal(PushAlertTypes.Invitation, alert.Type);
        Assert.Null(alert.MessageId);
        Assert.Equal(ConversationId, alert.ConversationId);
    }

    [Fact]
    public void An_invitation_to_a_group_that_cannot_be_named_says_a_group()
    {
        var alert = PushAlertText.ForInvitation(Notification(NotificationType.MemberAdded, "Alice"), ConversationId, groupName: null);

        Assert.Equal("Alice added you to a group", alert.Title);
    }
}
