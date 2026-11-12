using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Push;

// The words of an alert that is pushed to a browser that is away. The server cannot read what was said, and an alert passes through
// the push service on its way, so it says who sent it, in which group, and that there is a message or how many files it carries, and
// nothing of the text.
public class PushAlertTextTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();

    private static MessageDto Message(string sender = "Alice") => new(
        Guid.NewGuid(), ConversationId, Guid.NewGuid(), sender, FakeMessage.Ciphertext(1, "the words that must not leave"), 1,
        DateTime.UtcNow, Guid.NewGuid(), [], EditedAt: null, EditCount: 0, IsDeleted: false);

    private static NotificationDto Notification(NotificationType type, string actor = "Alice", Guid? messageId = null)
    {
        var message = messageId is null ? null : new NotificationMessageDto(FakeMessage.Ciphertext(1, "the words that must not leave"), 1, Guid.NewGuid(), 0);
        return new(Guid.NewGuid(), type, ConversationId, Guid.NewGuid(), actor, messageId, message, false, DateTime.UtcNow);
    }

    // ##### A message #####

    [Fact]
    public void A_message_in_a_group_is_titled_with_the_sender_and_the_group()
    {
        var alert = PushAlertText.ForMessage(Message("Alice"), "Weekend plans", fileCount: 0);

        Assert.Equal("Alice in Weekend plans", alert.Title);
    }

    [Fact]
    public void A_message_in_a_direct_conversation_is_titled_with_the_sender()
    {
        var alert = PushAlertText.ForMessage(Message("Alice"), groupName: null, fileCount: 0);

        Assert.Equal("Alice", alert.Title);
    }

    [Fact]
    public void A_message_says_that_there_is_one_and_nothing_of_what_it_says()
    {
        var alert = PushAlertText.ForMessage(Message(), "Weekend plans", fileCount: 0);

        Assert.Equal("New message", alert.Body);
        Assert.DoesNotContain("words", alert.Body);
        Assert.DoesNotContain("words", alert.Title);
    }

    [Fact]
    public void A_message_alert_leads_to_the_conversation_and_replaces_the_one_before_it()
    {
        var message = Message();

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
    public void A_message_with_files_says_how_many(int files, string body)
    {
        Assert.Equal(body, PushAlertText.ForMessage(Message(), "Team", files).Body);
    }

    // ##### A mention #####

    [Fact]
    public void A_mention_in_a_group_names_who_and_where_and_shows_no_text()
    {
        var messageId = Guid.NewGuid();
        var notification = Notification(NotificationType.Mention, "Alice", messageId);

        var alert = PushAlertText.ForMention(notification, ConversationId, "Weekend plans");

        Assert.Equal("Alice mentioned you in Weekend plans", alert.Title);
        Assert.Equal(string.Empty, alert.Body);
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
