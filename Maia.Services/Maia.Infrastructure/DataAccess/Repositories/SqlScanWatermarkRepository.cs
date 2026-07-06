using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlScanWatermarkRepository(IDbContextFactory<MaiaDbContext> factory)
    : IScanWatermarkRepository
{
    // ── File watermarks ──────────────────────────────────────────────────────

    public async Task<long> GetFileOffsetAsync(int monitoredJobId, string filePath, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanFileWatermarks
            .FirstOrDefaultAsync(w => w.MonitoredJobId == monitoredJobId && w.FilePath == filePath, ct);
        return wm?.ByteOffset ?? 0;
    }

    public async Task UpdateFileOffsetAsync(int monitoredJobId, int scanSourceId, string filePath, long byteOffset, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanFileWatermarks
            .FirstOrDefaultAsync(w => w.MonitoredJobId == monitoredJobId && w.FilePath == filePath, ct);

        if (wm is null)
            db.ScanFileWatermarks.Add(new ScanFileWatermark
            {
                MonitoredJobId = monitoredJobId,
                ScanSourceId   = scanSourceId,
                FilePath       = filePath,
                ByteOffset     = byteOffset,
                LastScannedAt  = DateTime.Now,
            });
        else
        {
            wm.ByteOffset    = byteOffset;
            wm.LastScannedAt = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Content watermarks (FileContent scans) ─────────────────────────────────

    public async Task<DateTime?> GetContentWatermarkAsync(int monitoredJobId, string filePath, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanContentWatermarks
            .FirstOrDefaultAsync(w => w.MonitoredJobId == monitoredJobId && w.FilePath == filePath, ct);
        return wm?.LastModifiedAt;
    }

    public async Task UpsertContentWatermarkAsync(int monitoredJobId, int scanSourceId, string filePath, DateTime lastModifiedAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanContentWatermarks
            .FirstOrDefaultAsync(w => w.MonitoredJobId == monitoredJobId && w.FilePath == filePath, ct);

        if (wm is null)
            db.ScanContentWatermarks.Add(new ScanContentWatermark
            {
                MonitoredJobId = monitoredJobId,
                ScanSourceId   = scanSourceId,
                FilePath       = filePath,
                LastModifiedAt = lastModifiedAt,
                LastScannedAt  = DateTime.Now,
            });
        else
        {
            wm.LastModifiedAt = lastModifiedAt;
            wm.LastScannedAt  = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Database watermarks ──────────────────────────────────────────────────

    public async Task<string?> GetDbWatermarkAsync(int checkRuleId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanDbWatermarks
            .FirstOrDefaultAsync(w => w.CheckRuleId == checkRuleId, ct);
        return wm?.WatermarkValue;
    }

    public async Task UpdateDbWatermarkAsync(int checkRuleId, string watermarkValue, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanDbWatermarks
            .FirstOrDefaultAsync(w => w.CheckRuleId == checkRuleId, ct);

        if (wm is null)
            db.ScanDbWatermarks.Add(new ScanDbWatermark
            {
                CheckRuleId    = checkRuleId,
                WatermarkValue = watermarkValue,
                LastScannedAt  = DateTime.Now,
            });
        else
        {
            wm.WatermarkValue = watermarkValue;
            wm.LastScannedAt  = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);
    }
}
