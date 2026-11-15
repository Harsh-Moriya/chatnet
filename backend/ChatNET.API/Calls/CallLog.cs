using ChatNET.API.Calls.Models;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Push;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Calls;

// Writes a call that has ended: the call itself and its row in the conversation's timeline, in one transaction, and tells the
// conversation. The row is made here, by the server, when the call ends (so it always lands at the bottom, after what was said
// during the call) and it is never signed or sealed: the server knew about the call already, since it relayed it.
//
// Recording a call twice is harmless. Two instances can both see the same call end (the person hung up as the ring timed out); the
// conversation's row lock, taken first, makes the second find the first's row and write nothing.
public class CallLog
{
    private readonly AppDbContext _db;
    private readonly OutboxPublisher _outbox;
    private readonly PushAlertRows _alerts;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<CallLog> _logger;

    public CallLog(
        AppDbContext db, OutboxPublisher outbox, PushAlertRows alerts, IHubContext<ChatHub, IChatClient> hub, ILogger<CallLog> logger)
    {
        _db = db;
        _outbox = outbox;
        _alerts = alerts;
        _hub = hub;
        _logger = logger;
    }

    // True when the call was written, false when it was already (nothing changes then) or its conversation does not exist.
    public async Task<bool> RecordAsync(Call call, CancellationToken cancellationToken)
    {
        var callerName = await _db.Users
            .Where(u => u.Id == call.CallerId)
            .Select(u => u.DisplayName)
            .FirstAsync(cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var seq = await ClaimAsync(call.ConversationId, call.EndedAt, cancellationToken);
        if (seq is null)
            return false;

        // After the lock above, so a record that committed first is seen. The transaction is rolled back when it is disposed, which
        // gives the number back.
        if (await _db.Calls.AnyAsync(c => c.Id == call.Id, cancellationToken))
            return false;

        // The id of the call is also the client message id of its row: it is unique per call, which is what the unique index on
        // (conversation, sender, client message id) needs of every row.
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = call.ConversationId,
            SenderId = call.CallerId,
            Kind = MessageKind.Call,
            CallId = call.Id,
            Ciphertext = null,
            KeyEpoch = 0,
            CreatedAt = call.EndedAt,
            ClientMessageId = call.Id,
            ChangeSeq = seq.Value,
        };

        var messageRow = _outbox.Create(new MessageCreated(message.Id, message.ConversationId));
        var callDto = new CallDto(call.Id, call.Media, call.StartedAt, call.AnsweredAt, call.EndedAt, call.EndReason);
        var alertRows = callDto.IsMissed ? _alerts.ForMessage(message, []) : [];

        _db.Calls.Add(call);
        _db.Messages.Add(message);
        _db.OutboxMessages.Add(messageRow);
        _db.OutboxMessages.AddRange(alertRows);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Call {CallId} of conversation {ConversationId} recorded as {EndReason}",
            call.Id, call.ConversationId, call.EndReason);

        var dto = new MessageDto(
            message.Id, message.ConversationId, message.SenderId, callerName, null, message.KeyEpoch, message.CreatedAt,
            message.ClientMessageId, [], EditedAt: null, EditCount: 0, IsDeleted: false, ChangeSeq: message.ChangeSeq,
            Kind: MessageKind.Call, Call: callDto);

        await _outbox.DeliverAsync(
            messageRow, () => _hub.Clients.Group(ChatGroups.Conversation(call.ConversationId)).ReceiveMessage(dto), cancellationToken);

        return true;
    }

    // Takes the next change number of the conversation and moves it up the sidebar, in the same statement that locks its row, as a
    // deletion does: whatever epoch the conversation is in, since a call row says nothing under a key. Null when there is no such
    // conversation.
    private async Task<long?> ClaimAsync(Guid conversationId, DateTime endedAt, CancellationToken cancellationToken)
    {
        var numbers = await _db.Database.SqlQuery<long>($"""
            UPDATE "Conversations"
            SET "UpdatedAt" = {endedAt}, "ChangeSeq" = "ChangeSeq" + 1
            WHERE "Id" = {conversationId}
            RETURNING "ChangeSeq" AS "Value"
            """).ToListAsync(cancellationToken);

        return numbers.Count == 0 ? null : numbers[0];
    }
}
