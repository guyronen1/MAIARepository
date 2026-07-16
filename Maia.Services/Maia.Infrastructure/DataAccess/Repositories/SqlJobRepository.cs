using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlJobRepository(IDbContextFactory<MaiaDbContext> factory) : IJobRepository
{
    public async Task<List<JobFailure>> GetByStatusAsync(JobStatus status, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures
            .Where(j => j.Status == status)
            .ToListAsync(ct);
    }

    public async Task<JobFailure?> GetByIdAsync(int failureId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures
            .Include(j => j.ErrorType)
            .Include(j => j.Recommendations)
            .Include(j => j.MonitoredJob)
            .FirstOrDefaultAsync(j => j.FailureId == failureId, ct);
    }

    public async Task<JobFailure> SaveAsync(JobFailure job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.JobFailures.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task UpdateStatusAsync(int failureId, JobStatus status, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.JobFailures.FindAsync([failureId], ct);
        if (job is null) return;
        job.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateClassificationAsync(
        int failureId, ClassificationResult result, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.JobFailures.FindAsync([failureId], ct);
        if (job is null) return;
        job.ErrorTypeId = result.ErrorTypeId;
        // Do NOT overwrite job.ErrorMessage — the scan strategy is authoritative for
        // what error was detected (e.g. for FS scans, the specific log line in the new
        // chunk past the watermark). The classifier's RawError already flows to the
        // recommendation's Explanation field via GenerateSuggestionsUseCase.
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<JobFailure>> GetUnclassifiedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures
            .Where(j => j.Status == JobStatus.Failed && j.ErrorTypeId == null)
            .ToListAsync(ct);
    }

    public async Task<bool> HasOpenFailureAsync(
        int monitoredJobId, string sourceTable, string targetField, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures.AnyAsync(f =>
            f.MonitoredJobId == monitoredJobId &&
            f.StepName       == sourceTable    &&
            f.Status         != JobStatus.Resolved, ct);
    }

    public async Task<HashSet<string>> GetOpenFailureSourceIdsAsync(
        int monitoredJobId, string stepName, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.JobFailures
            .Where(f => f.MonitoredJobId == monitoredJobId &&
                        f.StepName       == stepName       &&
                        f.Status         != JobStatus.Resolved &&
                        f.SourceId       != null)
            .Select(f => f.SourceId!)
            .Distinct()
            .ToListAsync(ct);
        // Case-insensitive: failures store lowercased GUIDs while the source row's
        // natural key may be uppercase — an ordinal match would miss and duplicate.
        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<PagedResult<JobFailure>> GetPagedAsync(
        int page, int pageSize, string? view = null, string? sort = null, string? dir = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        IQueryable<JobFailure> query = db.JobFailures
            .Include(j => j.JobType)
            .Include(j => j.ErrorType)
            .Include(j => j.MonitoredJob);

        // "fix-failed" window is today-midnight, matching the dashboard
        // "Fix Failures Today" KPI it drills into. Captured once so the EF
        // translation doesn't see DateTime.Today inside the Where expression.
        var todayStart = DateTime.Today;

        query = (view ?? string.Empty).ToLowerInvariant() switch
        {
            "active"          => query.Where(j => j.Status == JobStatus.Failed),
            "unclassified"    => query.Where(j => j.Status == JobStatus.Failed && j.ErrorTypeId == null),
            "awaiting-action" => query.Where(j => j.Status == JobStatus.Failed && j.ErrorTypeId != null),
            "resolved"        => query.Where(j => j.Status == JobStatus.Resolved),
            "manual-required" => query.Where(j => j.Status == JobStatus.ManualRequired),
            "auto-fixed"      => query.Where(j => db.FixExecutionLogs.Any(x =>
                                    x.FailureId == j.FailureId && x.Success && x.TriggerType == TriggerType.AutoHeal)),
            "operator-fixed"  => query.Where(j => db.FixExecutionLogs.Any(x =>
                                    x.FailureId == j.FailureId && x.Success && x.TriggerType == TriggerType.OperatorApproved)),
            // Failures the system tried to fix today and failed at — driven
            // by the dashboard's "Fix Failures Today" KPI drill-down. Status
            // is the durable signal (the executor flips JobStatus to
            // ManualRequired on a failed fix); the FixExecutionLog window
            // narrows to "today" so an old failure with a fresh failed log
            // is also surfaced if it ran again today.
            "fix-failed"      => query.Where(j =>
                                    j.Status == JobStatus.ManualRequired
                                 && db.FixExecutionLogs.Any(x =>
                                        x.FailureId == j.FailureId
                                     && !x.Success
                                     && x.ExecutedAt >= todayStart)),
            // Active failures the system can't act on for lack of config:
            // unclassified (no ErrorType) OR classified but no enabled
            // FixPolicyRule applies (override-then-default scope). Matches the
            // dashboard "Unconfigured" KPI count exactly.
            "unconfigured"    => query.Where(j =>
                                    j.Status == JobStatus.Failed
                                 && (j.ErrorTypeId == null
                                     || !db.FixPolicyRules.Any(p => p.Enabled
                                            && p.ErrorTypeId == j.ErrorTypeId
                                            && (p.MonitoredJobId == j.MonitoredJobId
                                                || (p.MonitoredJobId == null && p.JobTypeId == j.JobTypeId))))),
            _ => query, // null / "" / "all" / unknown → no filter
        };

        // Whitelisted sort — explicit column→expression map (no dynamic property
        // strings, so nothing operator-supplied reaches the SQL identifier).
        // Unknown/null key falls back to newest-first. A FailureId tiebreaker
        // makes paging deterministic when the primary key ties.
        var asc = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        IOrderedQueryable<JobFailure> ordered = (sort ?? string.Empty).ToLowerInvariant() switch
        {
            "id"        => asc ? query.OrderBy(j => j.FailureId)           : query.OrderByDescending(j => j.FailureId),
            "job"       => asc ? query.OrderBy(j => j.MonitoredJob!.Name)  : query.OrderByDescending(j => j.MonitoredJob!.Name),
            "errortype" => asc ? query.OrderBy(j => j.ErrorType!.Code)     : query.OrderByDescending(j => j.ErrorType!.Code),
            "status"    => asc ? query.OrderBy(j => j.Status)              : query.OrderByDescending(j => j.Status),
            "detected"  => asc ? query.OrderBy(j => j.DetectedAt)          : query.OrderByDescending(j => j.DetectedAt),
            _           => query.OrderByDescending(j => j.DetectedAt),
        };
        ordered = asc ? ordered.ThenBy(j => j.FailureId) : ordered.ThenByDescending(j => j.FailureId);

        var total = await ordered.CountAsync(ct);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<JobFailure>(items, total, page, pageSize);
    }

    public async Task<HashSet<int>> GetIdsWithRecentFixFailureAsync(
        IReadOnlyCollection<int> failureIds, DateTime since, CancellationToken ct = default)
    {
        if (failureIds.Count == 0) return new HashSet<int>();

        await using var db = await factory.CreateDbContextAsync(ct);
        var hits = await db.FixExecutionLogs
            .Where(x => failureIds.Contains(x.FailureId)
                     && !x.Success
                     && x.ExecutedAt >= since)
            .Select(x => x.FailureId)
            .Distinct()
            .ToListAsync(ct);
        return new HashSet<int>(hits);
    }
}
