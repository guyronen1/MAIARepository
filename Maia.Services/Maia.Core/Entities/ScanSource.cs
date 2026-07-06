using System.ComponentModel.DataAnnotations.Schema;
using Maia.Core.Enums;

namespace Maia.Core.Entities;

/// <summary>
/// A typed observation point within a <see cref="MonitoredJob"/> (Tier 2.5):
/// one ScanType + the scan config that used to live on MonitoredJob. A job has
/// many sources, so one operational concept can be watched via several paths
/// (a DB table, a log folder, another DB) and the dashboard rolls them up.
///
/// Phase (a) is behavior-preserving: backfill creates exactly one ScanSource per
/// existing MonitoredJob, mirroring its config 1:1. Leases stay 1:1 with the JOB
/// for now (per-source leases deferred); cadence/lease still live on MonitoredJob.
/// The 8 config columns are duplicated here while the old MonitoredJob columns
/// remain in place — a later cleanup migration drops them once code reads sources.
/// </summary>
public class ScanSource
{
    public int ScanSourceId   { get; set; }
    public int MonitoredJobId { get; set; }

    /// <summary>Operator-facing label for this source within the job, e.g. "FileSystem".</summary>
    public required string Name { get; set; }

    /// <summary>FK → ScanTypes. Replaces MonitoredJob.ScanTypeId as the per-source type.</summary>
    public int ScanTypeId { get; set; }
    public ScanTypeDefinition? ScanTypeDefinition { get; set; }

    /// <summary>Resolved from ScanTypeDefinition.Name — requires eager loading.</summary>
    [NotMapped]
    public ScanType ScanType
        => ScanTypeDefinition is null
               ? ScanType.FileSystem
               : Enum.Parse<ScanType>(ScanTypeDefinition.Name);

    // ── Scan config (mirrors MonitoredJob's columns during phase a) ────────────
    public string? LogFolder              { get; set; }
    public string? SearchPatterns         { get; set; }
    public string? InputFolder            { get; set; }
    public bool    IncludeSubfolders      { get; set; }
    public string? ConnectionName         { get; set; }
    public string? LogSourceUrl           { get; set; }
    public int     PollingIntervalSeconds { get; set; } = 300;
    public bool    IsActive               { get; set; } = true;

    public MonitoredJob? MonitoredJob { get; set; }
    public ICollection<ScanCheckRule> ScanCheckRules { get; set; } = [];
}
