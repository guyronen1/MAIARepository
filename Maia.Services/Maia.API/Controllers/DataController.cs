using Maia.API.Contracts;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// Read-only query endpoints for the monitoring dashboard.
/// Uses Core repository interfaces — no EF or Infrastructure types except a
/// read-only <c>IDbContextFactory</c> for aggregate dashboard stats.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireUser")]   // operational reads — any authenticated principal
public class DataController(
    IJobRepository                 jobs,
    IRecommendationRepository      recommendations,
    IMonitoredJobRepository        monitoredJobs,
    IScanRunHistoryRepository      scanRuns,
    IWorkerControlService          workerControl,
    IDbContextFactory<MaiaDbContext> dbFactory) : ControllerBase
{
    private const int MaxPageSize = 200;
    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendations(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var paged = await recommendations.GetPagedAsync(page, pageSize, ct);
        var dtos  = paged.Items.Select(RecommendationDto.From).ToList();
        return Ok(new { paged.TotalCount, paged.TotalPages, paged.Page, paged.PageSize, Items = dtos });
    }

    [HttpGet("failures/{failureId:int}/status")]
    public async Task<IActionResult> GetFailureStatus(int failureId, CancellationToken ct)
    {
        var f = await jobs.GetByIdAsync(failureId, ct);
        if (f is null)
            return NotFound(new { Message = $"JobFailure {failureId} not found." });

        var hasRecommendation = f.Recommendations.Any();
        var isExecuted        = f.Recommendations.Any(r => r.IsExecuted);

        // Stage pipeline. After "Recommended" there are two alternative
        // intermediate states the operator can reach:
        //   • AwaitingManualAction  → "Acknowledged" — operator approved a
        //                              Manual fix; off-system work in progress
        //   • ManualRequired        → "Manual" — operator rejected (or
        //                              auto-heal failed) and the failure now
        //                              needs operator's manual intervention
        // Both end at "Fixed" once the operator hits Mark Resolved.
        //
        // Status checks come BEFORE the legacy isExecuted fallback — otherwise
        // (a) the Acknowledged state would render as Fixed (IsExecuted is set
        // true at acknowledge time to stop the drain re-processing), and
        // (b) ManualRequired with a still-pending isExecuted=false rec would
        // wrongly fall through to "Recommended". Status is authoritative.
        var stage = f.Status == Maia.Core.Enums.JobStatus.Resolved              ? "Fixed"
                  : f.Status == Maia.Core.Enums.JobStatus.AwaitingManualAction  ? "Acknowledged"
                  : f.Status == Maia.Core.Enums.JobStatus.ManualRequired        ? "Manual"
                  : isExecuted                                                    ? "Fixed"
                  : hasRecommendation                                             ? "Recommended"
                  : f.ErrorTypeId.HasValue                                        ? "Classified"
                  :                                                                 "Failed";

        // Match the override-then-default lookup priority used by
        // SqlFixPolicyRepository / SqlRecommendationRepository so the
        // policyStepCount the operator sees here is the same one that would
        // execute on approve. One small query per failure-detail load.
        var policyInfo = await BuildPolicyInfoAsync(f, ct);

        // Execution history (incl. per-step rows for composite fixes). Surfaced
        // so the drawer can highlight a failed execution and show which step
        // succeeded vs failed. Composite step rows are written by
        // DefaultFixEngine.Composite (ExecutedBy ends ".Composite"); the
        // single summary row is written by ExecuteFixesUseCase. Ordered
        // chronologically (FixId is a monotonic shadow of ExecutedAt).
        List<object> executions;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Clean EF projection first (no enum.ToString() / object-cast inside
            // the expression tree — those can fail provider translation), then
            // shape to the response objects in memory.
            var rows = await db.FixExecutionLogs
                .Where(l => l.FailureId == failureId)
                .OrderBy(l => l.ExecutedAt).ThenBy(l => l.FixId)
                .Select(l => new
                {
                    l.FixId,
                    l.RecommendationId,
                    l.ExecutedAction,
                    l.ExecutedBy,
                    l.Success,
                    l.ResultDetail,
                    l.ExecutedAt,
                    l.TriggerType,
                })
                .ToListAsync(ct);

            executions = rows.Select(l => (object)new
            {
                l.FixId,
                l.RecommendationId,
                l.ExecutedAction,
                l.ExecutedBy,
                l.Success,
                l.ResultDetail,
                l.ExecutedAt,
                TriggerType = l.TriggerType.ToString(),
            }).ToList();
        }

        return Ok(new
        {
            f.FailureId,
            f.SourceId,
            // Input file path captured at scan time (FS: InputPathPattern,
            // DB: FilePathColumn). Surfaced so operators can see what
            // {sourceFilePath} resolves to for this failure before approving
            // a CopyFile / SQL fix that references it. Null when uncaptured.
            f.SourceFilePath,
            f.StepName,
            f.ErrorMessage,
            f.DetectedAt,
            Status           = f.Status.ToString(),
            Stage            = stage,
            ErrorTypeCode    = f.ErrorType?.Code,
            MonitoredJobName = f.MonitoredJob?.Name,
            Recommendations  = f.Recommendations.Select(r => new
            {
                r.RecommendationId,
                r.SuggestedAction,
                FixCategory    = r.FixCategory.ToString(),
                r.ConfidenceScore,
                r.AutoFixAvailable,
                r.OperatorApproved,
                r.IsExecuted,
                r.RecommendedAt,
                // Wire the live policy info — drives the rec card's composite
                // badge + step-list lazy fetch on the drawer.
                // PolicyActionType is what the drawer reads for Approve vs
                // Acknowledge: ActionType=Manual → no automation → Acknowledge.
                FixPolicyRuleId          = policyInfo.GetValueOrDefault(r.ErrorTypeId).RuleId,
                PolicyIsAutoHealEligible = policyInfo.GetValueOrDefault(r.ErrorTypeId).AutoHeal,
                PolicyStepCount          = policyInfo.GetValueOrDefault(r.ErrorTypeId).StepCount,
                PolicyActionType         = policyInfo.GetValueOrDefault(r.ErrorTypeId).ActionType,
            }).ToList(),
            Executions       = executions,
        });
    }

    /// <summary>
    /// One-shot lookup of "for each distinct ErrorTypeId on this failure's
    /// recommendations, what enabled policy would execute right now" —
    /// override (per-MonitoredJob) wins over default (per-JobType). Returns
    /// (ruleId, autoHeal, stepCount) per ErrorTypeId; missing entries mean
    /// no enabled policy matches.
    /// </summary>
    private async Task<Dictionary<int, (int? RuleId, bool? AutoHeal, int StepCount, string? ActionType)>>
        BuildPolicyInfoAsync(Maia.Core.Entities.JobFailure failure, CancellationToken ct)
    {
        var errorTypeIds = failure.Recommendations
            .Select(r => r.ErrorTypeId)
            .Distinct()
            .ToList();
        if (errorTypeIds.Count == 0)
            return new Dictionary<int, (int?, bool?, int, string?)>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var monitoredJobId = failure.MonitoredJobId;
        var jobTypeId      = failure.JobTypeId;

        // Project ActionType without .ToString() inside the EF expression tree
        // (enum.ToString() can fail provider translation — shape to string
        // post-materialization, same pattern as TriggerType in GetFailureStatus).
        var candidates = await db.FixPolicyRules
            .Where(p => p.Enabled && errorTypeIds.Contains(p.ErrorTypeId)
                     && ((monitoredJobId != null && p.MonitoredJobId == monitoredJobId)
                      || (p.MonitoredJobId == null && p.JobTypeId == jobTypeId)))
            .Select(p => new {
                p.RuleId, p.ErrorTypeId, p.MonitoredJobId,
                p.IsAutoHealEligible,
                p.ActionTimestamp,
                StepCount  = p.Steps.Count,
                p.ActionType,
            })
            .ToListAsync(ct);

        // For each ErrorTypeId pick the winning row: override (MonitoredJobId
        // non-null) beats default (null), then newest ActionTimestamp as
        // defensive tiebreaker. Mirrors SqlFixPolicyRepository.GetForAsync.
        var result = new Dictionary<int, (int? RuleId, bool? AutoHeal, int StepCount, string? ActionType)>();
        foreach (var etid in errorTypeIds)
        {
            var winner = candidates
                .Where(p => p.ErrorTypeId == etid)
                .OrderByDescending(p => p.MonitoredJobId != null)
                .ThenByDescending(p => p.ActionTimestamp)
                .FirstOrDefault();
            result[etid] = winner is null
                ? (null, null, 0, null)
                : (winner.RuleId, winner.IsAutoHealEligible, winner.StepCount, winner.ActionType.ToString());
        }
        return result;
    }

    [HttpGet("failures")]
    public async Task<IActionResult> GetFailures(
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 50,
        [FromQuery] string? view     = null,
        [FromQuery] string? sort     = null,
        [FromQuery] string? dir      = null,
        CancellationToken   ct       = default)
    {
        var paged = await jobs.GetPagedAsync(page, pageSize, view, sort, dir, ct);

        // Batch lookup: of the paged FailureIds, which ones have a Success=false
        // FixExecutionLog row since today-midnight? Drives the "Failed to
        // Execute" badge in the UI, independent of the active view filter
        // (so operators see the marker even when browsing the All view).
        var failureIds = paged.Items.Select(f => f.FailureId).ToList();
        var withFixFailure = await jobs.GetIdsWithRecentFixFailureAsync(
            failureIds, DateTime.Today, ct);

        var dtos = paged.Items
            .Select(f => JobFailureDto.From(f, withFixFailure.Contains(f.FailureId)))
            .ToList();
        return Ok(new { paged.TotalCount, paged.TotalPages, paged.Page, paged.PageSize, Items = dtos });
    }

    [HttpGet("monitored-jobs")]
    public async Task<IActionResult> GetMonitoredJobs(CancellationToken ct)
    {
        var all  = await monitoredJobs.GetActiveWithRulesAsync(ct);
        var dtos = all.Select(MonitoredJobDto.From).ToList();
        return Ok(dtos);
    }

    /// <summary>
    /// Liveness + active-scan snapshot for the dashboard. Polled every few seconds, so
    /// kept to a single SELECT that pulls the lease state for every active job, joined
    /// with its MonitoredJob + ScanType. Computes <c>aliveWindowSeconds</c> from the
    /// active-jobs' polling intervals so the client doesn't need its own threshold config.
    /// </summary>
    [HttpGet("worker-status")]
    public async Task<IActionResult> GetWorkerStatus(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var nowLocal = DateTime.Now;
        var recentCutoff = nowLocal.AddSeconds(-30);

        // Single round-trip: project just the fields we need (no Includes), shapes flat.
        var rows = await db.MonitoredJobLeases
            .Where(l => l.MonitoredJob != null && l.MonitoredJob.IsActive)
            .Select(l => new
            {
                l.MonitoredJobId,
                JobName                = l.MonitoredJob!.Name,
                ScanTypeName           = db.ScanSources
                                            .Where(s => s.MonitoredJobId == l.MonitoredJobId && s.IsActive)
                                            .OrderBy(s => s.ScanSourceId)
                                            .Select(s => s.ScanTypeDefinition != null ? s.ScanTypeDefinition.Name : "Unknown")
                                            .FirstOrDefault() ?? "Unknown",
                PollingIntervalSeconds = l.MonitoredJob.PollingIntervalSeconds,
                l.LeasedBy,
                l.LeasedAt,
                l.LeasedUntil,
                l.LastRunCompletedAt,
                l.LastRunOutcome,
            })
            .ToListAsync(ct);

        // Recent-completion window (last 30s) — what filled in between polls.
        // Joined to MonitoredJobs so the client can render job name without another lookup.
        var recentScans = await db.ScanRunHistory
            .Where(h => h.CompletedAt >= recentCutoff && h.MonitoredJob != null)
            .OrderByDescending(h => h.CompletedAt)
            .Take(20)
            .Select(h => new
            {
                scanRunId        = h.ScanRunId,
                monitoredJobId   = h.MonitoredJobId,
                jobName          = h.MonitoredJob!.Name,
                completedAt      = h.CompletedAt,
                durationMs       = h.DurationMs,
                outcome          = h.Outcome.ToString(),
                failuresDetected = h.FailuresDetected,
                classifications  = h.Classifications,
                recommendations  = h.Recommendations,
            })
            .ToListAsync(ct);

        // Per-job latest-scan summary for the dashboard's Monitored Jobs panel.
        // Correlated subquery uses IX_ScanRunHistory_Job_StartedAt for a seek + top-1.
        // Thin payload: only id + lastScan; name/scanType already live in the static
        // MonitoredJobDto the panel uses on initial load.
        var jobs = await db.MonitoredJobs
            .Where(m => m.IsActive)
            .Select(m => new
            {
                monitoredJobId = m.MonitoredJobId,
                // Job-level rollup (most recent run across all sources) — drives the
                // compact row's badge/duration/counts.
                lastScan = db.ScanRunHistory
                    .Where(h => h.MonitoredJobId == m.MonitoredJobId)
                    .OrderByDescending(h => h.StartedAt)
                    .Select(h => new
                    {
                        completedAt      = h.CompletedAt,
                        durationMs       = h.DurationMs,
                        outcome          = h.Outcome.ToString(),
                        failuresDetected = h.FailuresDetected,
                        classifications  = h.Classifications,
                        recommendations  = h.Recommendations,
                    })
                    .FirstOrDefault(),
                // Tier 2.5 (d2e): per-source last-scan breakdown for the drill-down.
                // Worker writes one ScanRunHistory row per source per tick, so the
                // latest row per ScanSourceId is that source's last scan.
                sources = m.ScanSources
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.ScanSourceId)
                    .Select(s => new
                    {
                        scanSourceId = s.ScanSourceId,
                        name         = s.Name,
                        scanTypeName = s.ScanTypeDefinition != null ? s.ScanTypeDefinition.Name : "Unknown",
                        lastScan = db.ScanRunHistory
                            .Where(h => h.ScanSourceId == s.ScanSourceId)
                            .OrderByDescending(h => h.StartedAt)
                            .Select(h => new
                            {
                                completedAt      = h.CompletedAt,
                                durationMs       = h.DurationMs,
                                outcome          = h.Outcome.ToString(),
                                failuresDetected = h.FailuresDetected,
                                classifications  = h.Classifications,
                                recommendations  = h.Recommendations,
                            })
                            .FirstOrDefault(),
                    })
                    .ToList(),
            })
            .ToListAsync(ct);

        var activeScans = rows
            .Where(r => r.LeasedBy != null && r.LeasedUntil.HasValue && r.LeasedUntil > nowLocal)
            .Select(r => new
            {
                monitoredJobId = r.MonitoredJobId,
                jobName        = r.JobName,
                scanType       = r.ScanTypeName,
                startedAt      = r.LeasedAt,
                leasedUntil    = r.LeasedUntil,
            })
            .ToList();

        // Alive-window threshold: 2 × max(PollingIntervalSeconds) across active jobs.
        // Fallback to 300s when there are no active jobs at all (defensive — keeps the
        // window finite for the workerAlive calculation below).
        var maxPolling = rows.Count > 0 ? rows.Max(r => r.PollingIntervalSeconds) : 300;
        var aliveWindowSeconds = maxPolling * 2;

        var lastCompletion = rows
            .Where(r => r.LastRunCompletedAt.HasValue)
            .Select(r => r.LastRunCompletedAt!.Value)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        var lastActivityAt = activeScans.Count > 0
            ? nowLocal
            : lastCompletion == DateTime.MinValue ? (DateTime?)null : lastCompletion;

        var workerAlive = activeScans.Count > 0
            || (lastActivityAt.HasValue
                && (nowLocal - lastActivityAt.Value).TotalSeconds < aliveWindowSeconds);

        var jobSummary = new
        {
            total   = rows.Count,
            active  = activeScans.Count,
            healthy = rows.Count(r => r.LastRunOutcome == JobRunOutcome.Success),
            failing = rows.Count(r => r.LastRunOutcome.HasValue
                                   && r.LastRunOutcome.Value != JobRunOutcome.Success),
        };

        return Ok(new
        {
            workerAlive,
            isPaused           = workerControl.IsPaused,
            lastActivityAt,
            aliveWindowSeconds,
            activeScans,
            recentScansLast30s = recentScans,
            jobSummary,
            jobs,
        });
    }

    /// <summary>
    /// Time-bucketed failure counts broken out by ErrorType, for the dashboard's
    /// Errors Over Time chart. <paramref name="range"/> selects the look-back window;
    /// <paramref name="bucketSize"/> defaults to hour for 24h and day for 7d/30d.
    /// Unclassified failures (ErrorTypeId IS NULL) collapse into a single
    /// "(unclassified)" series with errorTypeId=0 so the frontend can render them
    /// alongside the named series.
    /// </summary>
    [HttpGet("analytics/failures-over-time")]
    public async Task<IActionResult> GetFailuresOverTime(
        [FromQuery] string  range      = "24h",
        [FromQuery] string? bucketSize = null,
        CancellationToken   ct         = default)
    {
        // Range → look-back start (server-local). Use DateTime.Now (not UTC) to stay
        // consistent with the rest of the codebase per CLAUDE.md.
        var nowLocal = DateTime.Now;
        DateTime start;
        string effectiveBucket;
        switch ((range ?? "24h").ToLowerInvariant())
        {
            case "7d":  start = nowLocal.AddDays(-7);  effectiveBucket = bucketSize ?? "day";  break;
            case "30d": start = nowLocal.AddDays(-30); effectiveBucket = bucketSize ?? "day";  break;
            case "24h":
            default:    start = nowLocal.AddHours(-24); effectiveBucket = bucketSize ?? "hour"; break;
        }
        if (effectiveBucket != "hour" && effectiveBucket != "day")
            return BadRequest(new { Message = "bucketSize must be 'hour' or 'day'." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Server-side group by truncated DetectedAt + ErrorTypeId. EF translates
        // DateTime constructors to SQL date-part expressions in modern providers.
        // For SQL Server we lean on DATEPART/DATEADD via a switch on bucket size.
        var rows = effectiveBucket == "hour"
            ? await db.JobFailures
                .Where(f => f.DetectedAt >= start)
                .GroupBy(f => new
                {
                    BucketStart = new DateTime(f.DetectedAt.Year, f.DetectedAt.Month, f.DetectedAt.Day, f.DetectedAt.Hour, 0, 0),
                    f.ErrorTypeId,
                })
                .Select(g => new
                {
                    bucketStart = g.Key.BucketStart,
                    errorTypeId = g.Key.ErrorTypeId,
                    count       = g.Count(),
                })
                .ToListAsync(ct)
            : await db.JobFailures
                .Where(f => f.DetectedAt >= start)
                .GroupBy(f => new
                {
                    BucketStart = new DateTime(f.DetectedAt.Year, f.DetectedAt.Month, f.DetectedAt.Day),
                    f.ErrorTypeId,
                })
                .Select(g => new
                {
                    bucketStart = g.Key.BucketStart,
                    errorTypeId = g.Key.ErrorTypeId,
                    count       = g.Count(),
                })
                .ToListAsync(ct);

        // One round-trip for ErrorType metadata (used to display human names client-side).
        var errorTypeIds = rows.Where(r => r.errorTypeId.HasValue).Select(r => r.errorTypeId!.Value).Distinct().ToList();
        var errorTypes = await db.ErrorTypes
            .Where(et => errorTypeIds.Contains(et.ErrorTypeId))
            .ToDictionaryAsync(et => et.ErrorTypeId, et => new { et.Code, et.DisplayName }, ct);

        var result = rows
            .OrderBy(r => r.bucketStart)
            .ThenBy(r => r.errorTypeId)
            .Select(r =>
            {
                if (r.errorTypeId is int id && errorTypes.TryGetValue(id, out var et))
                {
                    return new
                    {
                        bucketStart      = r.bucketStart,
                        errorTypeId      = id,
                        errorTypeCode    = et.Code,
                        errorTypeDisplay = et.DisplayName,
                        count            = r.count,
                    };
                }
                return new
                {
                    bucketStart      = r.bucketStart,
                    errorTypeId      = 0,
                    errorTypeCode    = "(unclassified)",
                    errorTypeDisplay = "Unclassified",
                    count            = r.count,
                };
            })
            .ToList();

        return Ok(new
        {
            range          = range,
            bucketSize     = effectiveBucket,
            rangeStart     = start,
            rangeEnd       = nowLocal,
            buckets        = result,
        });
    }

    /// <summary>
    /// Top-N monitored jobs by failure count within the chosen range. Shares the
    /// 24h/7d/30d toggle with the Errors Over Time chart so a single operator
    /// gesture updates both row-1 charts. Filters out orphan failures with
    /// MonitoredJobId IS NULL — the chart is "top named jobs", not a mixed view.
    /// Tie-break alphabetical for stable UI ordering across renders.
    /// </summary>
    [HttpGet("analytics/failures-by-job")]
    public async Task<IActionResult> GetFailuresByJob(
        [FromQuery] string range = "24h",
        [FromQuery] int    limit = 10,
        CancellationToken  ct    = default)
    {
        // Clamp limit so an accidental ?limit=10000 doesn't blow the response.
        if (limit < 1)   limit = 1;
        if (limit > 50)  limit = 50;

        var nowLocal = DateTime.Now;
        DateTime start = (range ?? "24h").ToLowerInvariant() switch
        {
            "7d"  => nowLocal.AddDays(-7),
            "30d" => nowLocal.AddDays(-30),
            _     => nowLocal.AddHours(-24),
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // GroupBy MonitoredJobId then join MonitoredJob for display name.
        // Filter MonitoredJobId IS NOT NULL — top-10 lists named jobs only.
        var rows = await db.JobFailures
            .Where(f => f.DetectedAt >= start && f.MonitoredJobId != null)
            .GroupBy(f => f.MonitoredJobId!.Value)
            .Select(g => new { monitoredJobId = g.Key, failureCount = g.Count() })
            .ToListAsync(ct);

        var ids   = rows.Select(r => r.monitoredJobId).ToList();
        var jobs  = await db.MonitoredJobs
            .Where(j => ids.Contains(j.MonitoredJobId))
            .ToDictionaryAsync(j => j.MonitoredJobId, j => new { j.Name, j.DisplayName }, ct);

        var result = rows
            .Select(r =>
            {
                jobs.TryGetValue(r.monitoredJobId, out var j);
                return new
                {
                    monitoredJobId = r.monitoredJobId,
                    jobName        = j?.DisplayName ?? j?.Name ?? $"Job {r.monitoredJobId}",
                    failureCount   = r.failureCount,
                };
            })
            .OrderByDescending(r => r.failureCount)
            .ThenBy(r => r.jobName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// 7-day stacked-resolution-mix breakdown bucketed by JobFailure.DetectedAt.
    /// Single timestamp source for all four stacks keeps the semantic clear:
    /// "of failures detected on day X, here's the outcome composition".
    /// Today's bar honestly includes in-progress failures via the stillActive
    /// stack. Range param exists for future flexibility but only "7d" is wired.
    /// </summary>
    [HttpGet("analytics/resolution-mix")]
    public async Task<IActionResult> GetResolutionMix(
        [FromQuery] string range = "7d",
        CancellationToken  ct    = default)
    {
        // Range param accepted for future flexibility, but only 7d is wired now.
        if (range != "7d") range = "7d";

        // 7 days inclusive of today, server-local midnight boundaries.
        var todayStart = DateTime.Today;
        var windowStart = todayStart.AddDays(-6);
        var windowEnd   = todayStart.AddDays(1);   // exclusive upper bound

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Per-failure classification: project the AutoHeal / Operator flags
        // into a flat per-failure row, then group + count in memory. We can't
        // express `.Count(f => db.FixExecutionLogs.Any(...))` inside `.GroupBy`
        // — SQL Server rejects "aggregate over subquery". Per-row .Any() in
        // the SELECT translates to correlated subqueries (fine), and the
        // 7-day window keeps the in-memory grouping cheap.
        //
        // Caveat (inherited from dashboard-stats): a failure with BOTH a
        // successful AutoHeal log and a successful OperatorApproved log would
        // be counted in both columns. Idempotent suggestion generation makes
        // this rare in practice; documenting for parity with that endpoint.
        var raw = await db.JobFailures
            .Where(f => f.DetectedAt >= windowStart && f.DetectedAt < windowEnd)
            .Select(f => new
            {
                Day         = new DateTime(f.DetectedAt.Year, f.DetectedAt.Month, f.DetectedAt.Day),
                f.Status,
                HasAutoHeal = db.FixExecutionLogs.Any(x =>
                    x.FailureId == f.FailureId && x.Success && x.TriggerType == TriggerType.AutoHeal),
                HasOperator = db.FixExecutionLogs.Any(x =>
                    x.FailureId == f.FailureId && x.Success && x.TriggerType == TriggerType.OperatorApproved),
            })
            .ToListAsync(ct);

        var grouped = raw
            .GroupBy(f => f.Day)
            .ToDictionary(g => g.Key, g => new
            {
                autoHealed       = g.Count(f => f.HasAutoHeal),
                operatorApproved = g.Count(f => f.HasOperator),
                manualRequired   = g.Count(f => f.Status == JobStatus.ManualRequired),
                // "Still Active" uses the same predicate as the Active Failures
                // KPI on the dashboard (Status=Failed) so the two surfaces agree.
                stillActive      = g.Count(f => f.Status == JobStatus.Failed),
            });

        // Gap-fill: always return 7 rows, oldest first, so the chart can render
        // a 7-bar row even when some days had zero failures detected.
        var result = Enumerable.Range(0, 7)
            .Select(offset => windowStart.AddDays(offset))
            .Select(d => grouped.TryGetValue(d, out var r)
                ? new { bucketDay = d.ToString("yyyy-MM-dd"), r.autoHealed, r.operatorApproved, r.manualRequired, r.stillActive }
                : new { bucketDay = d.ToString("yyyy-MM-dd"), autoHealed = 0, operatorApproved = 0, manualRequired = 0, stillActive = 0 })
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// Aggregate counts for the dashboard. DB-level — does not depend on paging.
    /// <c>autoFixed</c> counts distinct failures with a successful auto-heal execution log;
    /// <c>manuallyFixed</c> counts distinct failures resolved via operator approval.
    /// </summary>
    [HttpGet("dashboard-stats")]
    public async Task<IActionResult> GetDashboardStats(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var totalFailures   = await db.JobFailures.CountAsync(ct);
        var active          = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Failed, ct);
        var resolved        = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Resolved, ct);
        var manualRequired  = await db.JobFailures.CountAsync(f => f.Status == JobStatus.ManualRequired, ct);
        var unclassified    = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Failed && f.ErrorTypeId == null, ct);
        var awaitingAction  = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Failed && f.ErrorTypeId != null, ct);

        var autoFixed       = await db.FixExecutionLogs
            .Where(x => x.Success && x.TriggerType == TriggerType.AutoHeal)
            .Select(x => x.FailureId).Distinct().CountAsync(ct);

        var manuallyFixed   = await db.FixExecutionLogs
            .Where(x => x.Success && x.TriggerType == TriggerType.OperatorApproved)
            .Select(x => x.FailureId).Distinct().CountAsync(ct);

        // Today-scoped fields for the "Resolved Today" KPI tile + its breakdown line.
        // "Today" = server-local midnight to now (DateTime.Today, not UTC) — matches
        // the local-time convention documented in CLAUDE.md.
        var todayStart      = DateTime.Today;
        var resolvedToday   = await db.JobFailures.CountAsync(
            f => f.Status == JobStatus.Resolved && f.DetectedAt >= todayStart, ct);
        var autoFixedToday  = await db.JobFailures
            .Where(f => f.DetectedAt >= todayStart)
            .CountAsync(f => db.FixExecutionLogs.Any(x =>
                x.FailureId == f.FailureId && x.Success && x.TriggerType == TriggerType.AutoHeal), ct);
        var manuallyFixedToday = await db.JobFailures
            .Where(f => f.DetectedAt >= todayStart)
            .CountAsync(f => db.FixExecutionLogs.Any(x =>
                x.FailureId == f.FailureId && x.Success && x.TriggerType == TriggerType.OperatorApproved), ct);

        // Fix Failures Today — distinct failures currently in ManualRequired
        // that had at least one Success=false FixExecutionLog since today-
        // midnight. Matches the `view=fix-failed` drill-down filter exactly
        // so the KPI count and the drill-down list always agree. Note: a
        // single failure with three failed step logs counts as 1 here
        // (Distinct/Any), not 3 — matches operator's mental model.
        var fixFailedToday = await db.JobFailures
            .Where(f => f.Status == JobStatus.ManualRequired
                     && db.FixExecutionLogs.Any(x =>
                            x.FailureId == f.FailureId
                         && !x.Success
                         && x.ExecutedAt >= todayStart))
            .CountAsync(ct);

        // Unconfigured — active (Failed) failures the system can't act on for
        // lack of config: either unclassified (no ErrorType — needs a
        // classification rule) OR classified but no enabled FixPolicyRule
        // applies for its ErrorType+scope (needs a fix policy). The "no policy"
        // arm mirrors the override-then-default lookup. Matches the
        // `view=unconfigured` drill-down predicate exactly. `unclassified`
        // (computed above) is the first arm; `unconfiguredNoPolicy` is the rest.
        var unconfigured = await db.JobFailures.CountAsync(f =>
            f.Status == JobStatus.Failed
            && (f.ErrorTypeId == null
                || !db.FixPolicyRules.Any(p => p.Enabled
                       && p.ErrorTypeId == f.ErrorTypeId
                       && (p.MonitoredJobId == f.MonitoredJobId
                           || (p.MonitoredJobId == null && p.JobTypeId == f.JobTypeId)))), ct);
        var unconfiguredNoPolicy = unconfigured - unclassified;

        return Ok(new
        {
            totalFailures,
            active,
            resolved,
            manualRequired,
            unclassified,
            awaitingAction,
            autoFixed,
            manuallyFixed,
            resolvedToday,
            autoFixedToday,
            manuallyFixedToday,
            fixFailedToday,
            unconfigured,
            unconfiguredNoPolicy,
        });
    }

    /// <summary>
    /// One row per completed worker-tick scan. Filterable by job, outcome, and date range.
    /// Default sort: StartedAt DESC. Default page size 50, max 200.
    /// </summary>
    [HttpGet("scan-runs")]
    public async Task<IActionResult> GetScanRuns(
        [FromQuery] int?      monitoredJobId,
        [FromQuery] int?      scanSourceId,
        [FromQuery] string?   outcome,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int       page     = 1,
        [FromQuery] int       pageSize = 50,
        CancellationToken     ct       = default)
    {
        if (page < 1)     page     = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        JobRunOutcome? outcomeFilter = null;
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (!Enum.TryParse<JobRunOutcome>(outcome, ignoreCase: true, out var parsed))
                return BadRequest(new { Message = $"Unknown outcome '{outcome}'. Expected one of: Success, Failed, Timeout, Stolen." });
            outcomeFilter = parsed;
        }

        var paged = await scanRuns.GetPagedAsync(
            monitoredJobId, scanSourceId, outcomeFilter, fromDate, toDate, page, pageSize, ct);
        var dtos  = paged.Items.Select(ScanRunDto.From).ToList();
        return Ok(new { paged.TotalCount, paged.TotalPages, paged.Page, paged.PageSize, Items = dtos });
    }

    /// <summary>
    /// Operator decision history — one row per Approve / Reject / Retry taken on a
    /// recommendation, newest first. Backs the Operator Actions screen (the
    /// "what did we decide and how did it end" audit view, distinct from the
    /// pending queue on /recommendations). Read-only join over
    /// OperatorActions → AIRecommendations → JobFailures.
    /// </summary>
    [HttpGet("operator-actions")]
    public async Task<IActionResult> GetOperatorActions(
        [FromQuery] string?   operatorId,
        [FromQuery] string?   actionTaken,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string?   q,
        [FromQuery] int       page     = 1,
        [FromQuery] int       pageSize = 50,
        CancellationToken     ct       = default)
    {
        if (page < 1)     page     = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.OperatorActions
            .AsNoTracking()
            .Include(a => a.Recommendation!).ThenInclude(r => r.ErrorType)
            .Include(a => a.Recommendation!).ThenInclude(r => r.Failure!).ThenInclude(f => f.MonitoredJob)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(operatorId))
            query = query.Where(a => a.OperatorId == operatorId);
        if (!string.IsNullOrWhiteSpace(actionTaken))
            query = query.Where(a => a.ActionTaken == actionTaken);
        if (fromDate.HasValue)
            query = query.Where(a => a.ActionTimestamp >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(a => a.ActionTimestamp <= toDate.Value);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(a =>
                a.Recommendation!.SuggestedAction.Contains(q)
                || (a.Recommendation.ErrorType != null && a.Recommendation.ErrorType.Code.Contains(q))
                || (a.Recommendation.Failure != null && a.Recommendation.Failure.MonitoredJob != null
                    && a.Recommendation.Failure.MonitoredJob.Name.Contains(q)));

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.ActionTimestamp)
            .ThenByDescending(a => a.ActionId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var dtos = items.Select(OperatorActionDto.From).ToList();
        return Ok(new { TotalCount = totalCount, TotalPages = totalPages, Page = page, PageSize = pageSize, Items = dtos });
    }
}
