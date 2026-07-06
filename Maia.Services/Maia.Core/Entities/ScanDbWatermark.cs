namespace Maia.Core.Entities;

/// <summary>
/// Tracks the last watermark value seen for a database ScanCheckRule so repeated scans
/// only process rows newer than the previous run.
/// WatermarkValue stores the last seen value of WatermarkColumn (as a string — SQL Server
/// handles implicit conversion for both integer IDs and datetime columns).
/// </summary>
public class ScanDbWatermark
{
    public int    WatermarkId    { get; set; }
    public int    CheckRuleId    { get; set; }
    public required string WatermarkValue { get; set; }
    public DateTime LastScannedAt { get; set; }

    public ScanCheckRule? CheckRule { get; set; }
}
