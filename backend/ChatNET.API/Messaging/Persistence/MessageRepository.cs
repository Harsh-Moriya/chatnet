using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Messaging.Persistence;

public class MessageRepository : IMessageRepository
{
    private readonly AppDbContext _db;

    public MessageRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Message message, CancellationToken cancellationToken)
    {
        _db.Messages.Add(message);

        // The conversation row is never loaded. A stub carrying only its key and the new UpdatedAt is
        // attached and that one property is marked modified, so EF sends a single-column UPDATE next
        // to the INSERT. SaveChanges runs its statements in one transaction, which keeps the message
        // and the conversation's place in the sidebar from ever disagreeing.
        var conversation = new Conversation { Id = message.ConversationId, UpdatedAt = message.CreatedAt };
        _db.Conversations.Attach(conversation);
        _db.Entry(conversation).Property(c => c.UpdatedAt).IsModified = true;

        await _db.SaveChangesAsync(cancellationToken);
    }
}
