namespace Maia.Core.Results;

public sealed class ClassificationResult
{
    public int FailureId { get; init; }
    public int JobId { get; init; }
    public int JobTypeId { get; init; }
    /// <summary>The MonitoredJob the failure belongs to. Optional because
    /// older / orphan JobFailures may have no MonitoredJobId. Used by the
    /// suggestion generator to pick a per-MonitoredJob fix override (when
    /// configured) over the JobType-level default.</summary>
    public int? MonitoredJobId { get; init; }
    public int ErrorTypeId { get; init; }
    public required string ErrorTypeCode { get; init; }
    public required string RawError { get; init; }
    public double Confidence { get; init; }
}
