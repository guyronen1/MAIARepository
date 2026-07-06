using Maia.API.Contracts;
using Maia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

/// <summary>
/// Operator-triggered maintenance endpoints. Keep this controller intentionally
/// small — anything that lives here bypasses the normal background schedules
/// and should be reserved for ops use.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "RequireAdmin")]
public class AdminController(
    IScanHistoryRetentionService retention,
    IWorkerControlService        workerControl,
    IAuditRepository             auditRepo) : ControllerBase
{
    /// <summary>
    /// Paged, filtered read of the audit log. Most-recent rows first.
    /// Filters are AND-combined; omit any to receive all values for that dimension.
    /// </summary>
    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] string?   entityType = null,
        [FromQuery] string?   entityId   = null,
        [FromQuery] string?   actor      = null,
        [FromQuery] string?   eventType  = null,
        [FromQuery] DateTime? fromDate   = null,
        [FromQuery] DateTime? toDate     = null,
        [FromQuery] int       page       = 1,
        [FromQuery] int       pageSize   = 50,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page     = Math.Max(1, page);

        var filter = new AuditLogFilter(entityType, entityId, actor, eventType, fromDate, toDate, page, pageSize);
        var result = await auditRepo.QueryAsync(filter, ct);

        return Ok(new
        {
            result.TotalCount,
            result.Page,
            result.PageSize,
            result.TotalPages,
            Items = result.Items.Select(AuditLogDto.From),
        });
    }

    /// <summary>
    /// Runs the ScanRunHistory retention sweep immediately.
    /// </summary>
    [HttpPost("scan-history/cleanup")]
    public async Task<IActionResult> RunScanHistoryCleanup(CancellationToken ct)
    {
        var result = await retention.SweepAsync(ct);
        return Ok(new
        {
            result.RowsDeleted,
            result.DurationMs,
            Cutoff  = result.Cutoff,
            Skipped = result.Skipped,
        });
    }

    /// <summary>Pauses the MonitoringWorker scan loop.</summary>
    [HttpPost("worker/pause")]
    public IActionResult PauseWorker()
    {
        workerControl.Pause();
        return Ok(new { isPaused = true });
    }

    /// <summary>Resumes the MonitoringWorker scan loop.</summary>
    [HttpPost("worker/resume")]
    public IActionResult ResumeWorker()
    {
        workerControl.Resume();
        return Ok(new { isPaused = false });
    }
}
