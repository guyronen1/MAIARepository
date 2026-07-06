namespace Maia.Core.Entities;

/// <summary>
/// Tracks the last byte offset read from a log file so repeated scans
/// only process newly-appended content rather than re-scanning the entire file.
/// </summary>
public class ScanFileWatermark
{
    public int    WatermarkId   { get; set; }
    public int    MonitoredJobId { get; set; }
    public int   ScanSourceId  { get; set; }
    public required string FilePath { get; set; }
    public long   ByteOffset    { get; set; }
    public DateTime LastScannedAt { get; set; }

    public MonitoredJob? MonitoredJob { get; set; }
    public ScanSource?   ScanSource   { get; set; }
}
