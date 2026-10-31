using ChatNET.API.Outbox.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Infrastructure;

// Reading the outbox the way a test wants to: the rows that are about one thing, found by the id in their payload.
public static class OutboxApi
{
    // The rows whose payload names this id under this property ("messageId", "conversationId", ...), oldest first.
    public static Task<List<OutboxMessage>> OutboxRowsAboutAsync(this ChatApiFactory factory, string property, Guid id) =>
        factory.QueryDbAsync(db => db.OutboxMessages
            .FromSqlInterpolated($"""SELECT * FROM "OutboxMessages" WHERE "Payload" ->> {property} = {id.ToString()}""")
            .AsNoTracking()
            .OrderBy(o => o.CreatedAt)
            .ToListAsync());

    public static Task<OutboxMessage> OutboxRowAsync(this ChatApiFactory factory, Guid rowId) =>
        factory.QueryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(o => o.Id == rowId));

    // Rows no relay or request has finished. Tests that leave some on purpose remove them, so a later test that
    // runs a relay pass is not handed another test's leftovers.
    public static Task RemoveWaitingOutboxRowsAsync(this ChatApiFactory factory) =>
        factory.ExecuteDbAsync(db => db.OutboxMessages.Where(o => o.DispatchedAt == null).ExecuteDeleteAsync());
}
