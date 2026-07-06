using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Results;

namespace Maia.Core.Interfaces;

/// <summary>
/// One implementation per ScanType.
/// Registered as IEnumerable&lt;IScanStrategy&gt; — consumers pick by ScanType.
/// </summary>
public interface IScanStrategy
{
    ScanType ScanType { get; }

    /// <summary>
    /// Scan one <see cref="ScanSource"/> of a job (Tier 2.5). The <paramref name="job"/>
    /// carries identity (JobTypeId / MonitoredJobId / Name) for the JobFailures this
    /// produces; the <paramref name="source"/> carries the scan config (folder /
    /// connection / url / …) and its own ScanCheckRules. The worker dispatches by
    /// <c>source.ScanType</c> and runs a job's sources sequentially under one lease.
    /// </summary>
    Task<ScanResult> ScanAsync(MonitoredJob job, ScanSource source, CancellationToken ct = default);
}
