using System.Text.Json;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Tests.Outbox;

// The parts of the outbox that need no database: what an event looks like as a payload, and which settings are accepted.
public class OutboxEventsTests
{
    // ##### Events #####

    [Fact]
    public void An_event_reads_back_as_what_was_written()
    {
        var message = new MessageCreated(Guid.NewGuid(), Guid.NewGuid());
        var notification = new NotificationCreated(Guid.NewGuid(), Guid.NewGuid());
        var updated = new MessageUpdated(Guid.NewGuid(), Guid.NewGuid());
        var conversation = new ConversationCreated(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]);
        var read = new ConversationRead(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 10, 31, 8, 30, 15, 123, DateTimeKind.Utc).AddTicks(450));

        Assert.Equal(message, OutboxEvents.Read(message.Kind, OutboxEvents.Serialize(message)));
        Assert.Equal(notification, OutboxEvents.Read(notification.Kind, OutboxEvents.Serialize(notification)));
        Assert.Equal(updated, OutboxEvents.Read(updated.Kind, OutboxEvents.Serialize(updated)));
        Assert.Equal(read, OutboxEvents.Read(read.Kind, OutboxEvents.Serialize(read)));

        var back = Assert.IsType<ConversationCreated>(
            OutboxEvents.Read(conversation.Kind, OutboxEvents.Serialize(conversation)));
        Assert.Equal(conversation.ConversationId, back.ConversationId);
        Assert.Equal(conversation.CreatorId, back.CreatorId);
        Assert.Equal(conversation.MemberIds, back.MemberIds);
    }

    [Fact]
    public void A_payload_holds_the_ids_by_name_and_no_kind()
    {
        var created = new MessageCreated(Guid.NewGuid(), Guid.NewGuid());

        using var payload = JsonDocument.Parse(OutboxEvents.Serialize(created));

        Assert.Equal(created.MessageId, payload.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(created.ConversationId, payload.RootElement.GetProperty("conversationId").GetGuid());
        Assert.False(payload.RootElement.TryGetProperty("kind", out _));
    }

    [Fact]
    public void Each_kind_is_its_own_name()
    {
        Assert.Equal("MessageCreated", new MessageCreated(Guid.Empty, Guid.Empty).Kind);
        Assert.Equal("MessageUpdated", new MessageUpdated(Guid.Empty, Guid.Empty).Kind);
        Assert.Equal("NotificationCreated", new NotificationCreated(Guid.Empty, Guid.Empty).Kind);
        Assert.Equal("ConversationCreated", new ConversationCreated(Guid.Empty, Guid.Empty, []).Kind);
        Assert.Equal("ConversationRead", new ConversationRead(Guid.Empty, Guid.Empty, default).Kind);
    }

    [Fact]
    public void A_kind_nobody_knows_and_an_empty_payload_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => OutboxEvents.Read("FromTheFuture", "{}"));
        Assert.Throws<InvalidOperationException>(() => OutboxEvents.Read(MessageCreated.Name, "null"));
    }

    // ##### Settings #####

    [Fact]
    public void The_default_settings_are_accepted()
    {
        Assert.True(new OutboxOptionsValidator().Validate(null, new OutboxOptions()).Succeeded);
    }

    [Theory]
    [InlineData(nameof(OutboxOptions.RelayInterval))]
    [InlineData(nameof(OutboxOptions.RetryDelay))]
    [InlineData(nameof(OutboxOptions.Retention))]
    [InlineData(nameof(OutboxOptions.CleanupInterval))]
    public void A_duration_that_is_not_positive_is_refused_and_named(string setting)
    {
        var options = new OutboxOptions();
        typeof(OutboxOptions).GetProperty(setting)!.SetValue(options, TimeSpan.Zero);

        var result = new OutboxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains($"Outbox:{setting}", result.FailureMessage);
    }

    [Theory]
    [InlineData(nameof(OutboxOptions.BatchSize))]
    [InlineData(nameof(OutboxOptions.MaxAttempts))]
    public void A_count_below_one_is_refused_and_named(string setting)
    {
        var options = new OutboxOptions();
        typeof(OutboxOptions).GetProperty(setting)!.SetValue(options, 0);

        var result = new OutboxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains($"Outbox:{setting}", result.FailureMessage);
    }

    [Fact]
    public void A_wait_before_the_relay_may_take_a_row_can_be_zero_but_not_negative()
    {
        var zero = new OutboxOptions { ClaimAfter = TimeSpan.Zero };
        var negative = new OutboxOptions { ClaimAfter = TimeSpan.FromSeconds(-1) };

        Assert.True(new OutboxOptionsValidator().Validate(null, zero).Succeeded);
        Assert.Contains("Outbox:ClaimAfter", new OutboxOptionsValidator().Validate(null, negative).FailureMessage);
    }
}
