namespace Maia.Core.Entities;

/// <summary>
/// Per-file processing watermark for FileContent scans. Unlike ScanFileWatermark
/// (byte offset for log tailing), content scans track WHOLE files: a file is
/// re-processed only when it's new or its last-modified time advanced past what
/// was recorded. Dedup rule: skip when current mtime &lt;= LastModifiedAt.
/// Content-hash tamper detection is deferred to v2 (mtime is the v1 signal).
/// </summary>
public class ScanContentWatermark
{
    public int      WatermarkId    { get; set; }
    public int      MonitoredJobId { get; set; }
    public int     ScanSourceId   { get; set; }
    public required string FilePath { get; set; }

    /// <summary>When MAIA last processed this file.</summary>
    public DateTime LastScannedAt  { get; set; }

    /// <summary>The file's last-write time captured at the last scan. The dedup
    /// comparison key — a file whose mtime exceeds this is treated as changed.</summary>
    public DateTime LastModifiedAt { get; set; }

    public MonitoredJob? MonitoredJob { get; set; }
    public ScanSource?   ScanSource   { get; set; }
}
