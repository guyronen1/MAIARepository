using Maia.Core.Entities;

namespace Maia.API.Contracts;

public sealed record JobFailureDto(
    int      FailureId,
    int      JobId,
    string?  StepName,
    string?  SourceId,
    string?  ErrorMessage,
    DateTime DetectedAt,
    string   Status,
    string   JobTypeName,
    string?  ErrorTypeCode,
    string?  MonitoredJobName,
    /// <summary>True when this failure has at least one
    /// <see cref="FixExecutionLog"/> row with <c>Success=false</c> since
    /// today-midnight. Drives the "Failed to Execute" marker in the
    /// failures list, independent of the current view filter.</summary>
    bool     HasRecentFixFailure)
{
    public static JobFailureDto From(JobFailure f, bool hasRecentFixFailure = false) => new(
        f.FailureId,
        f.JobId,
        f.StepName,
        f.SourceId,
        f.ErrorMessage,
        f.DetectedAt,
        f.Status.ToString(),
        f.JobType?.Name   ?? f.JobTypeId.ToString(),
        f.ErrorType?.Code,
        f.MonitoredJob?.Name,
        hasRecentFixFailure);
}
