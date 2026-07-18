using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// ScanSource CRUD (Tier 2.5 — a typed observation point within a job). Split out of
/// ConfigController; see <see cref="ConfigControllerBase"/>. Source-scoped scan-rule
/// creation lives on <c>ScanRulesConfigController</c>.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = "RequireOperator")]
public class ScanSourcesConfigController(
    IDbContextFactory<MaiaDbContext>      dbFactory,
    IAuditRepository                      audit,
    ICurrentUserAccessor                  currentUser,
    ILogger<ScanSourcesConfigController>  logger)
    : ConfigControllerBase(audit, currentUser, logger)
{
    /// <summary>
    /// Validates a source's config against its ScanType + cross-source constraints.
    /// Returns a 400 IActionResult on the first violation, else null.
    ///   • LogFolderRequired / ConnectionNameRequired / LogSourceUrlRequired — the
    ///     config field the source's type needs.
    ///   • IncludeSubfoldersInvalidForType — recursion only applies to file types.
    ///   • SourceNameRequired / SourceNameDuplicate — name present + unique among the
    ///     job's ACTIVE sources (case-insensitive via the DB's CI collation).
    ///   • UnknownScanType — ScanTypeId not in ScanTypes.
    ///   • SourceFolderConflict — two ACTIVE FS/FileContent sources of one job may not
    ///     share a LogFolder: watermarks are keyed (MonitoredJobId, FilePath), NOT
    ///     (ScanSourceId, FilePath), so they'd fight over the same watermark rows
    ///     (silent data loss). Guard lifts when watermarks are re-keyed to the source.
    /// </summary>
    private async Task<IActionResult?> ValidateScanSourceAsync(
        MaiaDbContext db, int jobId, UpsertScanSourceRequest req, int? existingSourceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "SourceNameRequired", message = "Source Name is required." });

        var st = await db.ScanTypes.FirstOrDefaultAsync(s => s.ScanTypeId == req.ScanTypeId, ct);
        if (st is null)
            return BadRequest(new { error = "UnknownScanType", message = $"ScanTypeId {req.ScanTypeId} not found." });
        var scanType   = Enum.Parse<ScanType>(st.Name);
        var isFileBased = scanType is ScanType.FileSystem or ScanType.FileContent;

        if (isFileBased && string.IsNullOrWhiteSpace(req.LogFolder))
            return BadRequest(new { error = "LogFolderRequired", message = $"{scanType} sources require a Log Folder." });
        if (scanType == ScanType.Database && string.IsNullOrWhiteSpace(req.ConnectionName))
            return BadRequest(new { error = "ConnectionNameRequired", message = "Database sources require a Connection Name." });
        if (scanType == ScanType.ApiEndpoint && string.IsNullOrWhiteSpace(req.LogSourceUrl))
            return BadRequest(new { error = "LogSourceUrlRequired", message = "ApiEndpoint sources require a URL." });
        if (req.IncludeSubfolders && !isFileBased)
            return BadRequest(new { error = "IncludeSubfoldersInvalidForType", message = "Include Subfolders applies only to FileSystem / FileContent sources." });

        var selfId = existingSourceId ?? 0;
        var name   = req.Name.Trim();
        if (await db.ScanSources.AnyAsync(s =>
                s.MonitoredJobId == jobId && s.IsActive && s.ScanSourceId != selfId && s.Name == name, ct))
            return BadRequest(new { error = "SourceNameDuplicate", message = $"This job already has an active source named '{name}'." });

        if (isFileBased && !string.IsNullOrWhiteSpace(req.LogFolder))
        {
            var folder = req.LogFolder.Trim().ToLower();
            var fileBasedTypeIds = await db.ScanTypes
                .Where(t => t.Name == "FileSystem" || t.Name == "FileContent")
                .Select(t => t.ScanTypeId).ToListAsync(ct);
            var conflict = await db.ScanSources.AnyAsync(s =>
                s.MonitoredJobId == jobId && s.IsActive && s.ScanSourceId != selfId
                && fileBasedTypeIds.Contains(s.ScanTypeId)
                && s.LogFolder != null && s.LogFolder.ToLower() == folder, ct);
            if (conflict)
                return BadRequest(new { error = "SourceFolderConflict",
                    message = "Cannot create a second source with the same LogFolder. Add additional rules to the existing source instead, or use a different folder." });
        }

        return null;
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs/{jobId:int}/scan-sources")]
    public async Task<IActionResult> CreateScanSource(int jobId, [FromBody] UpsertScanSourceRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.MonitoredJobs.FindAsync([jobId], ct) is null) return NotFound();
        if (await ValidateScanSourceAsync(db, jobId, req, existingSourceId: null, ct) is { } err) return err;

        var source = new ScanSource
        {
            MonitoredJobId    = jobId,
            Name              = req.Name.Trim(),
            ScanTypeId        = req.ScanTypeId,
            LogFolder         = req.LogFolder,
            SearchPatterns    = req.SearchPatterns,
            InputFolder       = req.InputFolder,
            IncludeSubfolders = req.IncludeSubfolders,
            ConnectionName    = req.ConnectionName,
            LogSourceUrl      = req.LogSourceUrl,
            IsActive          = true,
        };
        db.ScanSources.Add(source);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync("ScanSource", source.ScanSourceId.ToString(), "ScanSourceCreated", Actor,
            $"Created ScanSource '{source.Name}' for MonitoredJob {jobId} (ScanTypeId={source.ScanTypeId})", ct);
        return Ok(new { source.ScanSourceId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("scan-sources/{id:int}")]
    public async Task<IActionResult> UpdateScanSource(int id, [FromBody] UpsertScanSourceRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.ScanSources.FindAsync([id], ct);
        if (source is null) return NotFound();

        if (req.ScanTypeId != source.ScanTypeId)
            return BadRequest(new { error = "ScanTypeImmutable",
                message = "A source's ScanType cannot be changed. Delete the source and create a new one." });

        if (await ValidateScanSourceAsync(db, source.MonitoredJobId, req, existingSourceId: id, ct) is { } err) return err;

        var beforeName     = source.Name;
        var beforeFolder   = source.LogFolder;
        var beforePatterns = source.SearchPatterns;
        var beforeInput    = source.InputFolder;
        var beforeRecurse  = source.IncludeSubfolders;
        var beforeConn     = source.ConnectionName;
        var beforeUrl      = source.LogSourceUrl;
        var beforeActive   = source.IsActive;

        source.Name              = req.Name.Trim();
        source.LogFolder         = req.LogFolder;
        source.SearchPatterns    = req.SearchPatterns;
        source.InputFolder       = req.InputFolder;
        source.IncludeSubfolders = req.IncludeSubfolders;
        source.ConnectionName    = req.ConnectionName;
        source.LogSourceUrl      = req.LogSourceUrl;
        source.IsActive          = req.IsActive;
        await db.SaveChangesAsync(ct);

        var diff = BuildDiff(
            ("Name",              beforeName,     source.Name),
            ("LogFolder",         beforeFolder,   source.LogFolder),
            ("SearchPatterns",    beforePatterns, source.SearchPatterns),
            ("InputFolder",       beforeInput,    source.InputFolder),
            ("IncludeSubfolders", beforeRecurse,  source.IncludeSubfolders),
            ("ConnectionName",    beforeConn,     source.ConnectionName),
            ("LogSourceUrl",      beforeUrl,      source.LogSourceUrl),
            ("IsActive",          beforeActive,   source.IsActive));
        await WriteAuditAsync("ScanSource", id.ToString(), "ScanSourceUpdated", Actor,
            diff.Length > 0 ? diff : "No changes", ct);
        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("scan-sources/{id:int}")]
    public async Task<IActionResult> DeleteScanSource(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.ScanSources
            .Include(s => s.ScanCheckRules)
            .FirstOrDefaultAsync(s => s.ScanSourceId == id, ct);
        if (source is null) return NotFound();

        var name = source.Name; var typeId = source.ScanTypeId;
        // Soft-delete (matches the codebase pattern; the NoAction FKs block a hard
        // cascade and JobFailures reference ScanSourceId). Cascade soft-delete to the
        // source's active rules so none linger as "active under an inactive source".
        // Watermarks left dormant (the inactive source won't scan); JobFailures keep
        // their ScanSourceId for drill-down history.
        source.IsActive = false;
        var deactivated = 0;
        foreach (var r in source.ScanCheckRules.Where(r => r.IsActive)) { r.IsActive = false; deactivated++; }
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync("ScanSource", id.ToString(), "ScanSourceDeleted", Actor,
            $"Soft-deleted ScanSource {id} ('{name}', ScanTypeId={typeId}); deactivated {deactivated} rule(s)", ct);
        return NoContent();
    }
}
