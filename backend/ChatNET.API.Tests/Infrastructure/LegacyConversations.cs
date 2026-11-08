using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Tests.Infrastructure;

// Conversations as they were before keys existed: members and a name, no key epoch (KeyEpoch 0). Every conversation that was made
// before encryption looks like this, and the way to give one its first key is the same as for any other.
public static class LegacyConversations
{
    public static async Task<Guid> AddLegacyGroupAsync(this ChatApiFactory factory, Guid ownerId, string name, params Guid[] memberIds)
    {
        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            Type = ConversationType.Group,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedById = ownerId,
            Members =
            [
                new ConversationMember { UserId = ownerId, Role = MemberRole.Owner, JoinedAt = now },
                .. memberIds.Select(id => new ConversationMember { UserId = id, Role = MemberRole.Member, JoinedAt = now }),
            ],
        };

        await factory.ExecuteDbAsync(async db =>
        {
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        });

        return conversation.Id;
    }

    public static async Task<Guid> AddLegacyDirectAsync(this ChatApiFactory factory, Guid firstId, Guid secondId)
    {
        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            Type = ConversationType.Direct,
            DirectKey = DirectKey.For(firstId, secondId),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedById = firstId,
            Members =
            [
                new ConversationMember { UserId = firstId, Role = MemberRole.Member, JoinedAt = now },
                new ConversationMember { UserId = secondId, Role = MemberRole.Member, JoinedAt = now },
            ],
        };

        await factory.ExecuteDbAsync(async db =>
        {
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        });

        return conversation.Id;
    }
}
