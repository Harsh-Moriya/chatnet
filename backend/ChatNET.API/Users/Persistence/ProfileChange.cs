using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Outbox.Models;
using Microsoft.EntityFrameworkCore.Storage;

namespace ChatNET.API.Users.Persistence;

public sealed class ProfileChange : IProfileChange
{
    private readonly AppDbContext _db;
    private readonly IDbContextTransaction _transaction;

    public ProfileChange(AppDbContext db, IDbContextTransaction transaction, AppUser user)
    {
        _db = db;
        _transaction = transaction;
        User = user;
    }

    public AppUser User { get; }

    public async Task CommitAsync(IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken)
    {
        _db.OutboxMessages.AddRange(outbox);

        await _db.SaveChangesAsync(cancellationToken);
        await _transaction.CommitAsync(cancellationToken);
    }

    // Ending the transaction frees the advisory lock, whether it was committed or not. One that was not is rolled back.
    public ValueTask DisposeAsync() => _transaction.DisposeAsync();
}
