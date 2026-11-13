using System.Linq.Expressions;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Keys;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Messaging.Persistence;

public class MessageRepository : IMessageRepository
{
    // What is read of a message before it becomes a MessageDto: the blob is put into the form the API sends (base64url) in memory,
    // because the database cannot write that form. Only the page's senders are looked up, through the join, in the same query.
    private sealed record MessageRow(
        Guid Id,
        Guid ConversationId,
        Guid SenderId,
        string SenderDisplayName,
        byte[]? Ciphertext,
        int KeyEpoch,
        DateTime CreatedAt,
        Guid ClientMessageId,
        DateTime? EditedAt,
        int EditCount,
        bool IsDeleted,
        long ChangeSeq);

    private static readonly Expression<Func<Message, MessageRow>> ToRow = m => new MessageRow(
        m.Id,
        m.ConversationId,
        m.SenderId,
        m.Sender.DisplayName,
        m.Ciphertext,
        m.KeyEpoch,
        m.CreatedAt,
        m.ClientMessageId,
        m.EditedAt,
        m.EditCount,
        m.IsDeleted,
        m.ChangeSeq);

    // The files are filled in for the page as a whole afterwards (AttachmentLoader): one query for all of its files.
    private static MessageDto ToDto(MessageRow row) => new(
        row.Id,
        row.ConversationId,
        row.SenderId,
        row.SenderDisplayName,
        row.IsDeleted || row.Ciphertext is null ? null : KeyFormats.Encode(row.Ciphertext),
        row.KeyEpoch,
        row.CreatedAt,
        row.ClientMessageId,
        [],
        row.EditedAt,
        row.EditCount,
        row.IsDeleted,
        row.ChangeSeq);

    private readonly AppDbContext _db;

    public MessageRepository(AppDbContext db)
    {
        _db = db;
    }

    // One conditional UPDATE of the conversation decides whether the message may be stored under the epoch it was sealed in: the
    // conversation must be in that epoch and not marked for replacement. It also moves the conversation's UpdatedAt, which the
    // sidebar needs, and takes the next change number of the conversation (the message is stored under it), and it takes the row lock
    // that every change of the conversation's keys takes too, so a removal that marks the epoch as due and a send under it are settled
    // by Postgres: whichever commits second finds the first one's result and is refused. The lock is also what makes the numbers
    // consecutive and in commit order: no other change of the conversation can take the next number until this transaction ends.
    // A send that is refused, or that loses to the idempotency index, rolls back with the transaction and leaves no number unused.
    // The message, the sender's read position, the notifications and the outbox rows are saved in the same transaction.
    public async Task<AddOutcome> TryAddAsync(
        Message message, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var seq = await ClaimForSendAsync(message.ConversationId, message.KeyEpoch, message.CreatedAt, cancellationToken);
        if (seq is null)
            return await IsInEpochAsync(message.ConversationId, message.KeyEpoch, cancellationToken) ? AddOutcome.EpochDue : AddOutcome.EpochStale;

        message.ChangeSeq = seq.Value;
        _db.Messages.Add(message);
        _db.Notifications.AddRange(notifications);

        // The pushes this message causes are saved with it: either both are stored or neither is.
        _db.OutboxMessages.AddRange(outbox);

        // Sending counts as reading: whoever sends has seen the conversation up to their message. The conversation row is never
        // loaded; a stub carrying only its key and the new read position is attached and that one property is marked modified, so
        // EF sends a single-column UPDATE next to the INSERT. Besides being what people expect, it keeps the unread count cheap,
        // because the sender's own messages never accumulate after their read position for the count to step over.
        var sender = new ConversationMember
        {
            ConversationId = message.ConversationId,
            UserId = message.SenderId,
            LastReadAt = message.CreatedAt,
        };
        _db.ConversationMembers.Attach(sender);
        _db.Entry(sender).Property(m => m.LastReadAt).IsModified = true;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AddOutcome.Added;
        }
        catch (DbUpdateException ex) when (IsClientMessageClash(ex))
        {
            // The failed insert (and the stub next to it) stay in the change tracker; clear them so the caller can run further
            // queries on this context. The transaction is rolled back when it is disposed.
            _db.ChangeTracker.Clear();
            return AddOutcome.Repeat;
        }
    }

    // Only a clash on the client id index means "already sent". Any other unique violation is a different
    // problem and must not be mistaken for a retry.
    public static bool IsClientMessageClash(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: MessageConfiguration.ClientMessageIndexName,
        };

    // The three statements that take the next change number of a conversation. Each is one UPDATE ... RETURNING, so the number is
    // taken and the row lock is held in a single step, and the lock lasts until the transaction ends. Raw SQL because EF cannot
    // return the value an UPDATE wrote. Null means no row matched: the conversation is not in the epoch asked for, or is marked for
    // replacement (or does not exist).
    private async Task<long?> ClaimForSendAsync(Guid conversationId, int epoch, DateTime createdAt, CancellationToken cancellationToken)
    {
        var numbers = await _db.Database.SqlQuery<long>($"""
            UPDATE "Conversations"
            SET "UpdatedAt" = {createdAt}, "ChangeSeq" = "ChangeSeq" + 1
            WHERE "Id" = {conversationId} AND "KeyEpoch" = {epoch} AND NOT "KeyRotationRequired"
            RETURNING "ChangeSeq" AS "Value"
            """).ToListAsync(cancellationToken);

        return numbers.Count == 0 ? null : numbers[0];
    }

    // An edit does not move the conversation in the sidebar, so UpdatedAt is left alone. The same epoch conditions as a send: this
    // statement replaces the shared lock the edit used to take on the conversation, and is stronger, which is what keeps two edits that
    // each hold a shared lock from both waiting to take an exclusive one.
    private async Task<long?> ClaimForEditAsync(Guid conversationId, int epoch, CancellationToken cancellationToken)
    {
        var numbers = await _db.Database.SqlQuery<long>($"""
            UPDATE "Conversations"
            SET "ChangeSeq" = "ChangeSeq" + 1
            WHERE "Id" = {conversationId} AND "KeyEpoch" = {epoch} AND NOT "KeyRotationRequired"
            RETURNING "ChangeSeq" AS "Value"
            """).ToListAsync(cancellationToken);

        return numbers.Count == 0 ? null : numbers[0];
    }

    // A deletion is allowed in any epoch: it removes what was said, it does not say anything under a key.
    private async Task<long?> ClaimForDeleteAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var numbers = await _db.Database.SqlQuery<long>($"""
            UPDATE "Conversations"
            SET "ChangeSeq" = "ChangeSeq" + 1
            WHERE "Id" = {conversationId}
            RETURNING "ChangeSeq" AS "Value"
            """).ToListAsync(cancellationToken);

        return numbers.Count == 0 ? null : numbers[0];
    }

    // Why a conversation did not take a change: it is in another epoch (the sender went by what was true earlier, or the
    // conversation has none yet), or it is in that epoch and a removal or a leave marked it for replacement. True for the second.
    private async Task<bool> IsInEpochAsync(Guid conversationId, int epoch, CancellationToken cancellationToken)
    {
        var state = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new { c.KeyEpoch })
            .FirstOrDefaultAsync(cancellationToken);

        return state is not null && state.KeyEpoch == epoch;
    }

    public async Task<MessageDto?> FindByIdAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var row = await _db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(ToRow)
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToDto(row);
    }

    public Task<MessageForChange?> FindForChangeAsync(
        Guid conversationId, Guid messageId, DateTime since, CancellationToken cancellationToken)
        => _db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId && m.ConversationId == conversationId && m.CreatedAt >= since)
            .Select(m => new MessageForChange(m.Id, m.SenderId, m.CreatedAt, m.IsDeleted, m.EditCount, m.MentionedUserIds))
            .FirstOrDefaultAsync(cancellationToken);

    // Two requests racing for one message (an edit and a delete, two edits, an edit at the end of the window) are settled by
    // Postgres: the UPDATE carries every condition, so each statement is checked against what the other wrote. The edit count is
    // one of them: it is part of what the sender signed, so an edit made from an older version cannot be stored. The conversation is
    // claimed first, which checks its keys and takes its next change number under its row lock: that keeps a removal from marking the
    // epoch as due between this look and the commit, and orders this edit among the other changes of the conversation. An edit that
    // changes nothing returns before the commit, so its number is rolled back. The transaction keeps the new text, the notifications
    // and the rows that announce them together.
    public async Task<EditOutcome> TryEditAsync(
        Guid conversationId, Guid messageId, byte[] ciphertext, int keyEpoch, int editCount, DateTime editedAt, DateTime notBefore,
        IReadOnlyList<Guid> mentionedUserIds, IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var seq = await ClaimForEditAsync(conversationId, keyEpoch, cancellationToken);
        if (seq is null)
            return await IsInEpochAsync(conversationId, keyEpoch, cancellationToken) ? EditOutcome.EpochDue : EditOutcome.EpochStale;

        var number = seq.Value;
        var updated = await _db.Messages
            .Where(m => m.Id == messageId && !m.IsDeleted && m.CreatedAt > notBefore && m.EditCount == editCount - 1)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.Ciphertext, ciphertext)
                    .SetProperty(m => m.KeyEpoch, keyEpoch)
                    .SetProperty(m => m.EditCount, editCount)
                    .SetProperty(m => m.EditedAt, (DateTime?)editedAt)
                    .SetProperty(m => m.MentionedUserIds, mentionedUserIds.ToArray())
                    .SetProperty(m => m.ChangeSeq, number),
                cancellationToken);

        if (updated == 0)
            return await WhyNotEditedAsync(messageId, notBefore, cancellationToken);

        _db.Notifications.AddRange(notifications);
        _db.OutboxMessages.AddRange(outbox);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EditOutcome.Edited;
    }

    private async Task<EditOutcome> WhyNotEditedAsync(Guid messageId, DateTime notBefore, CancellationToken cancellationToken)
    {
        var now = await _db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.IsDeleted, m.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (now is null || now.IsDeleted)
            return EditOutcome.Deleted;

        return now.CreatedAt <= notBefore ? EditOutcome.TooLate : EditOutcome.EditCountStale;
    }

    // The conversation is claimed before the message is touched, so every change takes the locks in the same order (the conversation's
    // row, then the message's) and a deletion is numbered among the other changes of the conversation. A deletion that finds the
    // message already deleted returns before the commit and its number is rolled back.
    public async Task<bool> TryDeleteAsync(
        Guid conversationId, Guid messageId, Guid deletedById, DateTime deletedAt, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var seq = await ClaimForDeleteAsync(conversationId, cancellationToken);
        if (seq is null)
            return false;

        var number = seq.Value;
        var updated = await _db.Messages
            .Where(m => m.Id == messageId && m.ConversationId == conversationId && !m.IsDeleted)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.IsDeleted, true)
                    .SetProperty(m => m.DeletedAt, (DateTime?)deletedAt)
                    .SetProperty(m => m.DeletedById, (Guid?)deletedById)
                    .SetProperty(m => m.Ciphertext, (byte[]?)null)
                    .SetProperty(m => m.ChangeSeq, number),
                cancellationToken);

        if (updated == 0)
            return false;

        _db.OutboxMessages.AddRange(outbox);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    public async Task<MessageDto?> FindByClientMessageIdAsync(
        Guid conversationId, Guid senderId, Guid clientMessageId, CancellationToken cancellationToken)
    {
        var row = await _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.SenderId == senderId && m.ClientMessageId == clientMessageId)
            .Select(ToRow)
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToDto(row);
    }

    public async Task<MessageCursor?> FindCursorAsync(
        Guid conversationId, Guid messageId, MemberWindow window, CancellationToken cancellationToken)
    {
        var found = await InConversation(conversationId, window)
            .Where(m => m.Id == messageId)
            .Select(m => new { m.CreatedAt, m.Id })
            .FirstOrDefaultAsync(cancellationToken);

        return found is null ? null : new MessageCursor(found.CreatedAt, found.Id);
    }

    public Task<List<MessageDto>> GetLatestAsync(
        Guid conversationId, MemberWindow window, int take, CancellationToken cancellationToken)
        => ReadNewestFirstAsync(InConversation(conversationId, window), take, cancellationToken);

    // Positions are compared as (CreatedAt, Id). "CreatedAt <= t" comes first and is what the index
    // on (ConversationId, CreatedAt DESC) uses to start its walk; the id comparison only decides
    // among messages stamped exactly t, so the rows it filters out are a handful at most.
    public Task<List<MessageDto>> GetBeforeAsync(
        Guid conversationId, MemberWindow window, MessageCursor cursor, int take, CancellationToken cancellationToken)
        => ReadNewestFirstAsync(
            InConversation(conversationId, window).Where(m =>
                m.CreatedAt <= cursor.CreatedAt && (m.CreatedAt < cursor.CreatedAt || m.Id.CompareTo(cursor.Id) < 0)),
            take,
            cancellationToken);

    public async Task<List<MessageDto>> GetAfterAsync(
        Guid conversationId, MemberWindow window, MessageCursor cursor, int take, CancellationToken cancellationToken)
    {
        // Reading forwards already yields reading order, so the page is not turned around.
        var rows = await InConversation(conversationId, window)
            .Where(m => m.CreatedAt >= cursor.CreatedAt && (m.CreatedAt > cursor.CreatedAt || m.Id.CompareTo(cursor.Id) > 0))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(take)
            .Select(ToRow)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public Task<long?> GetChangeSeqAsync(Guid conversationId, CancellationToken cancellationToken)
        => _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => (long?)c.ChangeSeq)
            .FirstOrDefaultAsync(cancellationToken);

    // Walks the (ConversationId, ChangeSeq) index from the cursor, in order, so a page costs about its own rows whatever the size of
    // the conversation. The window is a filter on what the walk reaches: for someone who has been a member from the start there is
    // nothing to filter out.
    public async Task<List<MessageDto>> GetChangesAsync(
        Guid conversationId, MemberWindow window, long since, long upTo, int take, CancellationToken cancellationToken)
    {
        var rows = await InConversation(conversationId, window)
            .Where(m => m.ChangeSeq > since && m.ChangeSeq <= upTo)
            .OrderBy(m => m.ChangeSeq)
            .Take(take)
            .Select(ToRow)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    // The messages of the conversation the window lets through. The upper bound is added only when there is one, as a
    // plain range on CreatedAt, so Postgres starts its walk of the (ConversationId, CreatedAt DESC) index at the
    // moment the reader left. A condition that was always there ("or no bound") is only a range while Postgres plans
    // for the value it was given; once it has cached a plan for any value it is a filter on rows already walked past,
    // and a group that went on for years after someone left would cost them every one of them.
    private IQueryable<Message> InConversation(Guid conversationId, MemberWindow window)
    {
        var inWindow = _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.CreatedAt >= window.JoinedAt);

        return window.LeftAt is { } leftAt ? inWindow.Where(m => m.CreatedAt <= leftAt) : inWindow;
    }

    // Sorting newest first lets Postgres walk the (ConversationId, CreatedAt DESC) index from the
    // newest message and stop after "take" rows, whatever the size of the conversation. The id is the
    // second sort key so that messages with one timestamp always come in the same order, which is
    // the order the cursor comparison assumes. The page is turned around afterwards so the client
    // receives it in reading order.
    private static async Task<List<MessageDto>> ReadNewestFirstAsync(
        IQueryable<Message> messages, int take, CancellationToken cancellationToken)
    {
        var newestFirst = await messages
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(take)
            .Select(ToRow)
            .ToListAsync(cancellationToken);

        var page = newestFirst.Select(ToDto).ToList();
        page.Reverse();
        return page;
    }
}
