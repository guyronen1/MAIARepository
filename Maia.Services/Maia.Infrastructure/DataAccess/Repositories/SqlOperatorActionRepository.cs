using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlOperatorActionRepository(IDbContextFactory<MaiaDbContext> factory)
    : IOperatorActionRepository
{
    public async Task SaveAsync(OperatorAction action, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.OperatorActions.Add(action);
        await db.SaveChangesAsync(ct);
    }
}
