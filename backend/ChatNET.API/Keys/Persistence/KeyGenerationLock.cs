using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Keys.Persistence;

// A lock on the keys of one person, held until the transaction that took it ends. A new generation of someone's account key is
// stored under it exclusively, and everything that seals a conversation key to that key reads the generation under it shared. So
// an epoch that was made for the old generation is either stored before the new one is (and the conversations are then marked for
// replacement after it) or reads the new generation and is refused, and never lands in between.
//
// Advisory locks and not row locks: they do not interact with the foreign key checks of everything that references a person, so
// holding one cannot make a message send wait.
public static class KeyGenerationLock
{
    private static string Name(Guid userId) => "key-generation:" + userId;

    // Shared locks are taken in one order, so two requests that need several of the same people never wait for each other.
    public static async Task HoldSharedAsync(AppDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        foreach (var userId in userIds.Distinct().Order())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock_shared(hashtextextended({Name(userId)}, 0))", cancellationToken);
        }
    }

    public static async Task HoldExclusiveAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({Name(userId)}, 0))", cancellationToken);
    }
}
