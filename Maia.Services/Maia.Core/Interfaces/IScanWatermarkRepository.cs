namespace Maia.Core.Interfaces;

public interface IScanWatermarkRepository
{
    // ── File watermarks ──────────────────────────────────────────────────────
    /// <summary>Returns the last byte offset read for this file, or 0 if never scanned.</summary>
    Task<long> GetFileOffsetAsync(int monitoredJobId, string filePath, CancellationToken ct = default);
    Task UpdateFileOffsetAsync(int monitoredJobId, int scanSourceId, string filePath, long byteOffset, CancellationToken ct = default);

    // ── Database watermarks ──────────────────────────────────────────────────
    /// <summary>Returns the last watermark value seen for this rule, or null if never scanned.</summary>
    Task<string?> GetDbWatermarkAsync(int checkRuleId, CancellationToken ct = default);
    Task UpdateDbWatermarkAsync(int checkRuleId, string watermarkValue, CancellationToken ct = default);

    // ── Content watermarks (FileContent scans) ─────────────────────────────────
    /// <summary>Returns the file's last-modified time recorded at the last content
    /// scan, or null if this file was never processed. The strategy re-scans when
    /// the file's current mtime exceeds this value (or it's null).</summary>
    Task<DateTime?> GetContentWatermarkAsync(int monitoredJobId, string filePath, CancellationToken ct = default);

    /// <summary>Records that the file was processed, storing its last-modified time
    /// as the dedup key for the next scan. Upsert keyed on (monitoredJobId, filePath).</summary>
    Task UpsertContentWatermarkAsync(int monitoredJobId, int scanSourceId, string filePath, DateTime lastModifiedAt, CancellationToken ct = default);
}
