using Maia.Core.Analysis;
using Maia.Core.Enums;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// Operator visibility into MAIA's coverage gaps — failures it detected but
/// can't fully act on. Two read-only surfaces, both windowed (default 30 days,
/// "all" toggle):
///   • Case A (<c>clusters</c>): unclassified failures (no ErrorType matched),
///     grouped into suggested ClassificationRule patterns by the configured
///     <see cref="IUnconfiguredClusterAnalyzer"/>.
///   • Case B (<c>policy-gaps</c>): classified failures whose recommendation
///     has no effective FixPolicyRule (override→default lookup returns null),
///     aggregated by (ErrorType, JobType, MonitoredJob).
/// Read-only; the operator configures via the existing config endpoints.
/// </summary>
[ApiController]
[Route("api/unconfigured")]
[Authorize(Policy = "RequireUser")]   // operational reads
public class UnconfiguredController(
    IDbContextFactory<MaiaDbContext> dbFactory,
    IUnconfiguredClusterAnalyzer   analyzer) : ControllerBase
{
    /// <summary>Default lookback; "all" disables the window.</summary>
    private static DateTime? ResolveWindow(string? window)
        => string.Equals(window, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : DateTime.Now.AddDays(-30);

    // ── Case A: unclassified-failure clusters ────────────────────────────────
    [HttpGet("clusters")]
    public async Task<IActionResult> GetClusters([FromQuery] string window = "30d", CancellationToken ct = default)
    {
        var since = ResolveWindow(window);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var q = db.JobFailures.Where(f => f.Status == JobStatus.Failed && f.ErrorTypeId == null);
        if (since is not null) q = q.Where(f => f.DetectedAt >= since);

        var failures = await q
            .OrderByDescending(f => f.DetectedAt)
            .Select(f => new { f.FailureId, f.ErrorMessage })
            .ToListAsync(ct);

        var input    = failures.Select(f => new UnclassifiedFailure(f.FailureId, f.ErrorMessage ?? string.Empty)).ToList();
        var clusters = await analyzer.AnalyzeUnclassifiedAsync(input, ct);
        var clustered = clusters.Sum(c => c.FailureCount);

        return Ok(new
        {
            window             = since is null ? "all" : "30d",
            analyzerVersion    = analyzer.AnalyzerVersion,
            totalUnclassified  = input.Count,
            clusteredCount     = clustered,
            uncategorizedCount = input.Count - clustered,
            clusters,   // UnclassifiedCluster records (incl. normalizedSample, suggestedFromHash)
        });
    }

    // ── Case B: classified failures with no effective fix policy ─────────────
    [HttpGet("policy-gaps")]
    public async Task<IActionResult> GetPolicyGaps([FromQuery] string window = "30d", CancellationToken ct = default)
    {
        var since = ResolveWindow(window);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Keyed on rec.ErrorTypeId + failure JobType/MonitoredJob with the
        // override→default scope — exactly the key DefaultFixEngine uses at
        // execution time, so the gap list agrees with what actually fails.
        var recsQ = db.AIRecommendations
            .Join(db.JobFailures, r => r.FailureId, f => f.FailureId, (r, f) => new { r, f })
            // Only OPEN failures are real gaps — a Resolved / AwaitingManualAction /
            // ManualRequired failure has already been actioned and needs no policy.
            // Mirrors Case A's `Status == Failed` discipline (the gap list used to
            // count resolved failures, so marking one resolved didn't clear it).
            .Where(x => x.f.Status == JobStatus.Failed
                     && !db.FixPolicyRules.Any(p => p.Enabled
                          && p.ErrorTypeId == x.r.ErrorTypeId
                          && (p.MonitoredJobId == x.f.MonitoredJobId
                              || (p.MonitoredJobId == null && p.JobTypeId == x.f.JobTypeId))));
        if (since is not null) recsQ = recsQ.Where(x => x.f.DetectedAt >= since);

        var gaps = await recsQ
            .GroupBy(x => new { x.r.ErrorTypeId, x.f.JobTypeId, x.f.MonitoredJobId })
            .Select(g => new
            {
                g.Key.ErrorTypeId,
                g.Key.JobTypeId,
                g.Key.MonitoredJobId,
                Count           = g.Count(),
                SampleFailureId = g.Min(x => x.f.FailureId),   // anchor for the Case-B deep-link
            })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct);

        // Enrich with display names via one-shot lookups (avoids GroupBy-with-nav
        // translation pitfalls).
        var etNames = await db.ErrorTypes.Select(e => new { e.ErrorTypeId, e.Code }).ToDictionaryAsync(e => e.ErrorTypeId, e => e.Code, ct);
        var jtNames = await db.JobTypes.Select(j => new { j.JobTypeId, j.Name }).ToDictionaryAsync(j => j.JobTypeId, j => j.Name, ct);
        var mjIds   = gaps.Where(g => g.MonitoredJobId != null).Select(g => g.MonitoredJobId!.Value).Distinct().ToList();
        var mjNames = await db.MonitoredJobs.Where(m => mjIds.Contains(m.MonitoredJobId))
            .Select(m => new { m.MonitoredJobId, m.Name }).ToDictionaryAsync(m => m.MonitoredJobId, m => m.Name, ct);

        var rows = gaps.Select(g => new
        {
            g.ErrorTypeId,
            errorTypeCode    = etNames.GetValueOrDefault(g.ErrorTypeId, g.ErrorTypeId.ToString()),
            g.JobTypeId,
            jobTypeName      = jtNames.GetValueOrDefault(g.JobTypeId, g.JobTypeId.ToString()),
            g.MonitoredJobId,
            monitoredJobName = g.MonitoredJobId != null ? mjNames.GetValueOrDefault(g.MonitoredJobId.Value) : null,
            count            = g.Count,
            g.SampleFailureId,
        }).ToList();

        return Ok(new
        {
            window = since is null ? "all" : "30d",
            totalGaps = rows.Sum(r => r.count),
            gaps = rows,
        });
    }
}
