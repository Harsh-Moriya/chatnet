using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Keys;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Conversations.Persistence;

public class KeyEpochRepository : IKeyEpochRepository
{
    private readonly AppDbContext _db;

    public KeyEpochRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DateTime?> GetCreatedAtAsync(Guid conversationId, int epoch, CancellationToken cancellationToken) =>
        await _db.ConversationKeyEpochs
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.Epoch == epoch)
            .Select(e => (DateTime?)e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<List<KeyEpochDto>> GetForMemberAsync(
        Guid conversationId, Guid userId, int fromEpoch, int take, CancellationToken cancellationToken)
    {
        var mine = await (
            from epoch in _db.ConversationKeyEpochs
            join envelope in _db.ConversationKeyEnvelopes
                on new { epoch.ConversationId, epoch.Epoch } equals new { envelope.ConversationId, envelope.Epoch }
            where epoch.ConversationId == conversationId && envelope.UserId == userId && epoch.Epoch >= fromEpoch
            orderby epoch.Epoch
            select new
            {
                epoch.Epoch,
                epoch.CreatedById,
                epoch.Reason,
                epoch.CreatedAt,
                epoch.KeyCommitment,
                epoch.Signature,
                envelope.AccountKeyGeneration,
                envelope.Envelope,
            })
            .AsNoTracking()
            .Take(take)
            .ToListAsync(cancellationToken);

        if (mine.Count == 0)
            return [];

        // Who each epoch was made for, without their envelopes: only the caller's is returned, so no one else's is read.
        var epochs = mine.Select(e => e.Epoch).ToList();
        var members = (await _db.ConversationKeyEnvelopes
                .AsNoTracking()
                .Where(v => v.ConversationId == conversationId && epochs.Contains(v.Epoch))
                .OrderBy(v => v.UserId)
                .Select(v => new { v.Epoch, v.UserId, v.AccountKeyGeneration })
                .ToListAsync(cancellationToken))
            .ToLookup(v => v.Epoch, v => new EpochMemberDto(v.UserId, v.AccountKeyGeneration));

        return mine
            .Select(e => new KeyEpochDto(
                e.Epoch,
                e.CreatedById,
                e.Reason,
                e.CreatedAt,
                KeyFormats.Encode(e.KeyCommitment),
                KeyFormats.Encode(e.Signature),
                members[e.Epoch].ToList(),
                e.AccountKeyGeneration,
                KeyFormats.Encode(e.Envelope)))
            .ToList();
    }
}
