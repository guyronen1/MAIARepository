using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// Lookup data (job-types) + ErrorType CRUD. Split out of ConfigController; see
/// <see cref="ConfigControllerBase"/>. Reads are Operator, writes Admin.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = "RequireOperator")]
public class ErrorTypesConfigController(
    IDbContextFactory<MaiaDbContext>    dbFactory,
    IAuditRepository                    audit,
    ICurrentUserAccessor                currentUser,
    ILogger<ErrorTypesConfigController> logger)
    : ConfigControllerBase(audit, currentUser, logger)
{
    // ── Lookup data ──────────────────────────────────────────────────────────

    [HttpGet("job-types")]
    public async Task<IActionResult> GetJobTypes(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var types = await db.JobTypes.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync(ct);
        return Ok(types.Select(t => new { t.JobTypeId, t.Name, t.Description }));
    }

    [HttpGet("error-types")]
    public async Task<IActionResult> GetErrorTypes(
        [FromQuery] bool includeInactive = false,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = includeInactive ? db.ErrorTypes.AsQueryable() : db.ErrorTypes.Where(t => t.IsActive);
        var types = await q.OrderBy(t => t.Code).ToListAsync(ct);
        return Ok(types.Select(t => new
        {
            t.ErrorTypeId, t.Code, t.DisplayName, t.Description,
            Severity = t.Severity.ToString(),
            t.IsActive,
        }));
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("error-types")]
    public async Task<IActionResult> CreateErrorType([FromBody] UpsertErrorTypeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code))        return BadRequest(new { Message = "Code is required." });
        if (string.IsNullOrWhiteSpace(req.DisplayName)) return BadRequest(new { Message = "DisplayName is required." });
        if (!Enum.TryParse<Severity>(req.Severity, ignoreCase: true, out var severity))
            return BadRequest(new { Message = $"Unknown Severity '{req.Severity}'. Expected: Low, Medium, High, Critical." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ErrorTypes.AnyAsync(t => t.Code == req.Code, ct))
            return Conflict(new { Message = $"ErrorType with Code '{req.Code}' already exists." });

        var et = new ErrorType
        {
            Code        = req.Code,
            DisplayName = req.DisplayName,
            Description = req.Description,
            Severity    = severity,
            IsActive    = req.IsActive,
        };
        db.ErrorTypes.Add(et);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ErrorType",
            entityId:   et.ErrorTypeId.ToString(),
            eventType:  "ErrorTypeCreated",
            actor:      Actor,
            detail:     $"Created ErrorType '{et.Code}' (DisplayName='{et.DisplayName}', Severity={et.Severity}, IsActive={FormatValue(et.IsActive)})",
            ct: ct);

        return Ok(new { et.ErrorTypeId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("error-types/{id:int}")]
    public async Task<IActionResult> UpdateErrorType(int id, [FromBody] UpsertErrorTypeRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<Severity>(req.Severity, ignoreCase: true, out var severity))
            return BadRequest(new { Message = $"Unknown Severity '{req.Severity}'. Expected: Low, Medium, High, Critical." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var et = await db.ErrorTypes.FindAsync([id], ct);
        if (et is null) return NotFound();

        // Code is the natural key — block accidental collisions on rename
        if (!string.Equals(et.Code, req.Code, StringComparison.Ordinal)
            && await db.ErrorTypes.AnyAsync(t => t.Code == req.Code && t.ErrorTypeId != id, ct))
            return Conflict(new { Message = $"ErrorType with Code '{req.Code}' already exists." });

        // Snapshot original values BEFORE mutation so the audit diff sees
        // the actual before/after — EF Core's tracked entity is about to
        // be overwritten in place.
        var beforeCode        = et.Code;
        var beforeDisplayName = et.DisplayName;
        var beforeDescription = et.Description;
        var beforeSeverity    = et.Severity;
        var beforeIsActive    = et.IsActive;

        et.Code        = req.Code;
        et.DisplayName = req.DisplayName;
        et.Description = req.Description;
        et.Severity    = severity;
        et.IsActive    = req.IsActive;
        await db.SaveChangesAsync(ct);

        var diff = BuildDiff(
            ("Code",        beforeCode,        et.Code),
            ("DisplayName", beforeDisplayName, et.DisplayName),
            ("Description", beforeDescription, et.Description),
            ("Severity",    beforeSeverity,    et.Severity),
            ("IsActive",    beforeIsActive,    et.IsActive));
        await WriteAuditAsync(
            entityType: "ErrorType",
            entityId:   id.ToString(),
            eventType:  "ErrorTypeUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("error-types/{id:int}")]
    public async Task<IActionResult> DeleteErrorType(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var et = await db.ErrorTypes.FindAsync([id], ct);
        if (et is null) return NotFound();

        // Soft delete — referenced from JobFailures / ClassificationRules / FixPolicyRules / AIRecommendations
        // with RESTRICT FKs. A hard DELETE would fail; flipping IsActive is the right primitive.
        et.IsActive = false;
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ErrorType",
            entityId:   id.ToString(),
            eventType:  "ErrorTypeDeleted",
            actor:      Actor,
            detail:     $"Soft-deleted ErrorType {id} ('{et.Code}', DisplayName='{et.DisplayName}')",
            ct: ct);

        return NoContent();
    }
}
