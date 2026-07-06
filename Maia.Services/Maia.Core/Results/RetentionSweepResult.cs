namespace Maia.Core.Results;

/// <summary>
/// Outcome of one ScanRunHistory retention sweep. <c>Skipped=true</c> when the
/// feature is disabled via config; the other fields are zero/now in that case.
/// </summary>
public sealed record RetentionSweepResult(
    int      RowsDeleted,
    int      DurationMs,
    DateTime Cutoff,
    bool     Skipped);
