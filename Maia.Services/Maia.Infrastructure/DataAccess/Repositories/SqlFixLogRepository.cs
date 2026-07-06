using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlFixLogRepository(IDbContextFactory<MaiaDbContext> factory) : IFixLogRepository
{
    public async Task SaveAsync(FixExecutionLog log, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.FixExecutionLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }
}
