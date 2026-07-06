using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlAuditRepository(IDbContextFactory<MaiaDbContext> factory) : IAuditRepository
{
    public async Task WriteAsync(AuditLog audit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<AuditLog>> QueryAsync(AuditLogFilter filter, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        IQueryable<AuditLog> q = db.AuditLogs.OrderByDescending(a => a.AuditId);

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
            q = q.Where(a => a.EntityType == filter.EntityType);
        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            q = q.Where(a => a.EntityId == filter.EntityId);
        if (!string.IsNullOrWhiteSpace(filter.Actor))
            q = q.Where(a => a.Actor.Contains(filter.Actor));
        if (!string.IsNullOrWhiteSpace(filter.EventType))
            q = q.Where(a => a.EventType == filter.EventType);
        if (filter.FromDate.HasValue)
            q = q.Where(a => a.Timestamp >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)
            q = q.Where(a => a.Timestamp < filter.ToDate.Value.AddDays(1));

        var total = await q.CountAsync(ct);
        var items = await q
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);

        return new PagedResult<AuditLog>(items, total, filter.Page, filter.PageSize);
    }
}
