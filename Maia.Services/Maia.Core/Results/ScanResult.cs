using Maia.Core.Enums;

namespace Maia.Core.Results;

public sealed class ScanResult
{
    public required string   JobName          { get; init; }
    public required ScanType ScanType         { get; init; }
    public int               FailuresDetected { get; set; }
    public int               Classifications  { get; set; }
    public int               Recommendations  { get; set; }

    /// <summary>FileContent scans — matched files where IdentifierLocator was set
    /// but yielded nothing, so SourceId fell back to the filename. The worker
    /// copies this onto the ScanRunHistory row. 0 for other scan types.</summary>
    public int               IdentifierExtractionFailures { get; set; }

    /// <summary>FileContent scans — files skipped this scan for exceeding the
    /// extraction size cap. Copied onto ScanRunHistory by the worker.</summary>
    public int               OversizeFileSkips { get; set; }

    /// <summary>FileContent scans — rules skipped because a predicate was set but
    /// the ExtractorLocator yielded no value to test (valid locator, value absent
    /// in the file, or unparseable). Surfaces "the predicate couldn't be
    /// evaluated" rather than failing silently. Copied onto ScanRunHistory.</summary>
    public int               PredicateUnevaluableSkips { get; set; }

    public string?           Detail           { get; set; }
}
