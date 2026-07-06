namespace Maia.Core.Entities;

/// <summary>
/// Lookup table for scan types (FileSystem, Database, ApiEndpoint, FileContent).
/// ScanSource.ScanTypeId is a FK to this table.
/// </summary>
public class ScanTypeDefinition
{
    public int    ScanTypeId  { get; set; }
    public required string Name        { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// How long a worker is allowed to hold a lease on a job whose sources include
    /// this scan type. Sets the per-job execution timeout too.
    /// FileSystem ≈ 300s, Database ≈ 1800s (long scans), ApiEndpoint ≈ 60s.
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 300;
}
