using Maia.Core.Entities;

namespace Maia.API.Contracts;

public sealed record ScanRunDto(
    int      ScanRunId,
    int      MonitoredJobId,
    string?  MonitoredJobName,
    // Tier 2.5: which source this run scanned (null on pre-migration rows).
    int?     ScanSourceId,
    string?  ScanSourceName,
    string   LeasedBy,
    DateTime StartedAt,
    DateTime CompletedAt,
    int      DurationMs,
    string   Outcome,
    string?  Error,
    int      FailuresDetected,
    int      Classifications,
    int      Recommendations,
    // FileContent diagnostics — 0 for other scan types. Surfaced so a
    // misconfigured locator / missing value / oversize file is visible in
    // scan history instead of only in the log file.
    int      IdentifierExtractionFailures,
    int      OversizeFileSkips,
    int      PredicateUnevaluableSkips)
{
    public static ScanRunDto From(ScanRunHistory r) => new(
        r.ScanRunId,
        r.MonitoredJobId,
        r.MonitoredJob?.Name,
        r.ScanSourceId,
        r.ScanSource?.Name,
        r.LeasedBy,
        r.StartedAt,
        r.CompletedAt,
        r.DurationMs,
        r.Outcome.ToString(),
        r.Error,
        r.FailuresDetected,
        r.Classifications,
        r.Recommendations,
        r.IdentifierExtractionFailures,
        r.OversizeFileSkips,
        r.PredicateUnevaluableSkips);
}
