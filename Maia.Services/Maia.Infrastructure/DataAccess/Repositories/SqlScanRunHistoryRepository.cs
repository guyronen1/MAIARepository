using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlScanRunHistoryRepository(IDbContextFactory<MaiaDbContext> factory)
    : IScanRunHistoryRepository
{
    public async Task SaveAsync(ScanRunHistory run, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ScanRunHistory.Add(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<ScanRunHistory>> GetPagedAsync(
        int? monitoredJobId,
        int? scanSourceId,
        JobRunOutcome? outcome,
        DateTime? fromDate,
        DateTime? toDate,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Include the source so the DTO can show which source the run scanned
        // (Tier 2.5). MonitoredJob still included for the job name.
        IQueryable<ScanRunHistory> q = db.ScanRunHistory
            .Include(r => r.MonitoredJob)
            .Include(r => r.ScanSource);

        if (monitoredJobId.HasValue) q = q.Where(r => r.MonitoredJobId == monitoredJobId.Value);
        if (scanSourceId.HasValue)   q = q.Where(r => r.ScanSourceId == scanSourceId.Value);
        if (outcome.HasValue)        q = q.Where(r => r.Outcome == outcome.Value);
        if (fromDate.HasValue)       q = q.Where(r => r.StartedAt >= fromDate.Value);
        if (toDate.HasValue)         q = q.Where(r => r.StartedAt <= toDate.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ScanRunHistory>(items, total, page, pageSize);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoff, int batchSize, CancellationToken ct = default)
    {
        // Raw SQL because EF Core's ExecuteDeleteAsync with Take() emits an awkward
        // subquery on SQL Server; DELETE TOP (N) ... WHERE ... is the natural form.
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlRawAsync(
            "DELETE TOP (@batchSize) FROM ScanRunHistory WHERE CompletedAt < @cutoff",
            new[]
            {
                new SqlParameter("@batchSize", batchSize),
                new SqlParameter("@cutoff",    cutoff),
            },
            ct);
    }
}
