using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Membership;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;
using Microsoft.EntityFrameworkCore.Storage;

namespace ChatNET.API.Conversations.Persistence;

public sealed class MembershipChange : IMembershipChange
{
    private readonly AppDbContext _db;
    private readonly IDbContextTransaction _transaction;

    public MembershipChange(AppDbContext db, IDbContextTransaction transaction, Conversation conversation)
    {
        _db = db;
        _transaction = transaction;
        Conversation = conversation;
        Members = new GroupMembers(conversation.Id, conversation.Members);
    }

    public Conversation Conversation { get; }

    public GroupMembers Members { get; }

    public async Task CommitAsync(
        IReadOnlyList<Notification> notifications, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken)
    {
        _db.Notifications.AddRange(notifications);
        _db.OutboxMessages.AddRange(outbox);

        await _db.SaveChangesAsync(cancellationToken);
        await _transaction.CommitAsync(cancellationToken);
    }

    // Ending the transaction frees the advisory lock, whether it was committed or not. One that was not is rolled back.
    public ValueTask DisposeAsync() => _transaction.DisposeAsync();
}
