using Maia.Core.Enums;

namespace Maia.Core.Entities;

/// <summary>
/// One row per completed worker-tick scan of a MonitoredJob. Append-only — never
/// updated after insert. Bounded by the ScanHistoryRetentionWorker (default 30 days).
/// </summary>
public class ScanRunHistory
{
    public int      ScanRunId        { get; set; }
    public int      MonitoredJobId   { get; set; }
    public int     ScanSourceId     { get; set; }
    /// <summary>Worker identity that owned the lease for this run ("host=...;pid=...;runId=...").</summary>
    public required string LeasedBy  { get; set; }
    public DateTime StartedAt        { get; set; }
    public DateTime CompletedAt      { get; set; }
    /// <summary>Convenience field — CompletedAt minus StartedAt in ms.</summary>
    public int      DurationMs       { get; set; }
    public JobRunOutcome Outcome     { get; set; }
    public string?  Error            { get; set; }
    public int      FailuresDetected { get; set; }
    public int      Classifications  { get; set; }
    public int      Recommendations  { get; set; }

    /// <summary>FileContent scans — count of matched files where IdentifierLocator
    /// was set but extraction yielded nothing, so SourceId fell back to the
    /// filename. Surfaces a misconfigured IdentifierLocator without log-diving.
    /// 0 for other scan types.</summary>
    public int      IdentifierExtractionFailures { get; set; }

    /// <summary>FileContent scans — count of files skipped this scan because they
    /// exceeded the 5MB extraction cap. 0 for other scan types.</summary>
    public int      OversizeFileSkips { get; set; }

    /// <summary>FileContent scans — count of rules skipped because a predicate was
    /// set but the ExtractorLocator yielded no value to test (value absent or
    /// unparseable in the file). 0 for other scan types.</summary>
    public int      PredicateUnevaluableSkips { get; set; }

    public MonitoredJob? MonitoredJob { get; set; }
    public ScanSource?   ScanSource   { get; set; }
}
