using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// ClassificationRule CRUD — both the global rules (JobType-level) and the per-job
/// create/link/unlink relationship endpoints. Split out of ConfigController; see
/// <see cref="ConfigControllerBase"/>.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = "RequireOperator")]
public class ClassificationRulesConfigController(
    IClassificationRuleRepository                ruleRepo,
    IDbContextFactory<MaiaDbContext>             dbFactory,
    IAuditRepository                             audit,
    ICurrentUserAccessor                         currentUser,
    ILogger<ClassificationRulesConfigController> logger)
    : ConfigControllerBase(audit, currentUser, logger)
{
    // ── Per-job Classification Rules ────────────────────────────────────────

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs/{jobId:int}/classification-rules")]
    public async Task<IActionResult> CreateJobClassificationRule(
        int jobId, [FromBody] UpsertJobClassificationRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.MonitoredJobs.FindAsync([jobId], ct);
        if (job is null) return NotFound();

        // Same duplicate guard as CreateClassificationRule — UX_ClassificationRules_ActiveKey
        // is on (JobTypeId, Pattern) WHERE IsActive=1, so an existing global rule for this
        // job's JobType will conflict. Return 409 with the conflicting rule id so the UI
        // can offer "Link the existing rule" instead of silently crashing.
        var dupId = await FindActiveClassificationDuplicateAsync(db, job.JobTypeId, req.Pattern, null, ct);
        if (dupId is not null)
            return Conflict(new
            {
                error             = "DuplicateClassificationRule",
                message           = $"An enabled classification rule with this pattern already exists for this job type (rule {dupId}). Link the existing rule instead of creating a duplicate.",
                conflictingRuleId = dupId,
            });

        var rule = new ClassificationRule
        {
            JobTypeId   = job.JobTypeId,
            ErrorTypeId = req.ErrorTypeId,
            Pattern     = req.Pattern,
            Confidence  = req.Confidence,
            Priority    = req.Priority,
            IsActive    = req.IsActive,
            CreatedBy   = Actor,
        };
        db.ClassificationRules.Add(rule);
        await db.SaveChangesAsync(ct);

        db.MonitoredJobRules.Add(new MonitoredJobRule
        {
            MonitoredJobId = jobId,
            RuleId         = rule.RuleId,
            IsActive       = true,
        });
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   rule.RuleId.ToString(),
            eventType:  "ClassificationRuleCreated",
            actor:      Actor,
            detail:     $"Created ClassificationRule for MonitoredJob {jobId} (ErrorTypeId={req.ErrorTypeId}, Pattern='{rule.Pattern}', Confidence={rule.Confidence}, Priority={rule.Priority}) and linked it",
            ct: ct);

        return Ok(new { rule.RuleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs/{jobId:int}/classification-rules/{ruleId:int}/link")]
    public async Task<IActionResult> LinkJobClassificationRule(
        int jobId, int ruleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.MonitoredJobs.FindAsync([jobId], ct) is null) return NotFound("Job not found");
        if (await db.ClassificationRules.FindAsync([ruleId], ct) is null) return NotFound("Rule not found");
        var exists = await db.MonitoredJobRules
            .AnyAsync(r => r.MonitoredJobId == jobId && r.RuleId == ruleId, ct);
        if (exists) return Conflict("Rule already linked to this job");
        db.MonitoredJobRules.Add(new MonitoredJobRule { MonitoredJobId = jobId, RuleId = ruleId, IsActive = true });
        await db.SaveChangesAsync(ct);

        // Linking is a relationship change, not an entity edit; audit it
        // against the MonitoredJob so an auditor scanning a job's history
        // sees both job-level and link-level events together.
        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   jobId.ToString(),
            eventType:  "ClassificationRuleLinked",
            actor:      Actor,
            detail:     $"Linked ClassificationRule {ruleId} to MonitoredJob {jobId}",
            ct: ct);

        return Ok(new { ruleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("monitored-jobs/{jobId:int}/classification-rules/{ruleId:int}")]
    public async Task<IActionResult> DeleteJobClassificationRule(
        int jobId, int ruleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var link = await db.MonitoredJobRules
            .FirstOrDefaultAsync(r => r.MonitoredJobId == jobId && r.RuleId == ruleId, ct);
        if (link is null) return NotFound();
        db.MonitoredJobRules.Remove(link);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   jobId.ToString(),
            eventType:  "ClassificationRuleUnlinked",
            actor:      Actor,
            detail:     $"Unlinked ClassificationRule {ruleId} from MonitoredJob {jobId}",
            ct: ct);

        return NoContent();
    }

    // ── Global Classification Rules ──────────────────────────────────────────

    [HttpGet("classification-rules")]
    public async Task<IActionResult> GetAllClassificationRules(CancellationToken ct)
    {
        var rules = await ruleRepo.GetAllAsync(ct);

        // Active job links per rule → drives the "Scope" column: no links =
        // JobType default (all jobs of the type); links = scoped to those jobs.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var links = await (from m in db.MonitoredJobRules
                           where m.IsActive
                           join j in db.MonitoredJobs on m.MonitoredJobId equals j.MonitoredJobId
                           select new { m.RuleId, j.Name }).ToListAsync(ct);
        var linkedJobsByRule = links
            .GroupBy(l => l.RuleId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n).ToList());

        return Ok(rules.Select(r => new
        {
            r.RuleId, r.JobTypeId,
            JobTypeName  = r.JobType?.Name ?? r.JobTypeId.ToString(),
            r.ErrorTypeId,
            ErrorTypeCode = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.Pattern, r.Confidence, r.Priority, r.IsActive, r.CreatedBy,
            LinkedJobNames = linkedJobsByRule.GetValueOrDefault(r.RuleId, new List<string>()),
        }));
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("classification-rules")]
    public async Task<IActionResult> CreateClassificationRule([FromBody] UpsertClassificationRuleRequest req, CancellationToken ct)
    {
        // Duplicate guard (backend layer): at most one ENABLED rule per
        // (JobTypeId, Pattern). Returns an actionable 409 so the UI can offer
        // "open existing" instead of the operator silently creating a copy
        // (which the /unconfigured retry-on-no-effect flow did 4× in practice).
        await using (var dupDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var dupId = await FindActiveClassificationDuplicateAsync(dupDb, req.JobTypeId, req.Pattern, null, ct);
            if (dupId is not null)
                return Conflict(new
                {
                    error = "DuplicateClassificationRule",
                    message = $"An enabled classification rule with this pattern already exists for this job type (rule {dupId}).",
                    conflictingRuleId = dupId,
                });
        }

        var rule = new ClassificationRule
        {
            JobTypeId   = req.JobTypeId,
            ErrorTypeId = req.ErrorTypeId,
            Pattern     = req.Pattern,
            Confidence  = req.Confidence,
            Priority    = req.Priority,
            IsActive    = true,
            CreatedBy   = Actor,
            // Provenance when accepted from an /unconfigured cluster (else null).
            SuggestedBy         = req.SuggestedBy,
            SuggestedFromHash   = req.SuggestedFromHash,
            SuggestedConfidence = req.SuggestedConfidence,
        };
        var saved = await ruleRepo.SaveAsync(rule, ct);

        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   saved.RuleId.ToString(),
            eventType:  "ClassificationRuleCreated",
            actor:      Actor,
            detail:     $"Created ClassificationRule (JobTypeId={saved.JobTypeId}, ErrorTypeId={saved.ErrorTypeId}, Pattern='{saved.Pattern}', Confidence={saved.Confidence}, Priority={saved.Priority})",
            ct: ct);

        return Ok(new { saved.RuleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("classification-rules/{id:int}")]
    public async Task<IActionResult> UpdateClassificationRule(int id, [FromBody] UpsertClassificationRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ClassificationRules.FindAsync([id], ct);
        if (rule is null) return NotFound();

        // Duplicate guard — only an ENABLED rule can collide. Excludes self.
        if (req.IsActive)
        {
            var dupId = await FindActiveClassificationDuplicateAsync(db, req.JobTypeId, req.Pattern, id, ct);
            if (dupId is not null)
                return Conflict(new
                {
                    error = "DuplicateClassificationRule",
                    message = $"An enabled classification rule with this pattern already exists for this job type (rule {dupId}).",
                    conflictingRuleId = dupId,
                });
        }

        var beforeJobTypeId   = rule.JobTypeId;
        var beforeErrorTypeId = rule.ErrorTypeId;
        var beforePattern     = rule.Pattern;
        var beforeConfidence  = rule.Confidence;
        var beforePriority    = rule.Priority;
        var beforeIsActive    = rule.IsActive;

        rule.JobTypeId   = req.JobTypeId;
        rule.ErrorTypeId = req.ErrorTypeId;
        rule.Pattern     = req.Pattern;
        rule.Confidence  = req.Confidence;
        rule.Priority    = req.Priority;
        rule.IsActive    = req.IsActive;

        await db.SaveChangesAsync(ct);

        var diff = BuildDiff(
            ("JobTypeId",   beforeJobTypeId,   rule.JobTypeId),
            ("ErrorTypeId", beforeErrorTypeId, rule.ErrorTypeId),
            ("Pattern",     beforePattern,     rule.Pattern),
            ("Confidence",  beforeConfidence,  rule.Confidence),
            ("Priority",    beforePriority,    rule.Priority),
            ("IsActive",    beforeIsActive,    rule.IsActive));
        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   id.ToString(),
            eventType:  "ClassificationRuleUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("classification-rules/{id:int}")]
    public async Task<IActionResult> DeleteClassificationRule(int id, CancellationToken ct)
    {
        // Capture identifying fields before the hard delete so the audit
        // row remains intelligible after the rule is gone.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ClassificationRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RuleId == id, ct);
        if (rule is null) return NotFound();
        var rulePattern = rule.Pattern;

        await ruleRepo.DeleteAsync(id, ct);

        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   id.ToString(),
            eventType:  "ClassificationRuleDeleted",
            actor:      Actor,
            detail:     $"Deleted ClassificationRule {id} (Pattern='{rulePattern}')",
            ct: ct);

        return NoContent();
    }

    /// <summary>
    /// Returns the RuleId of an ENABLED ClassificationRule with the same
    /// (JobTypeId, Pattern) — the active-key duplicate. Case-insensitive via
    /// the DB collation (matches the unique index + the classifier's matching).
    /// <paramref name="excludeRuleId"/> skips self on update.
    /// </summary>
    private static async Task<int?> FindActiveClassificationDuplicateAsync(
        MaiaDbContext db, int jobTypeId, string pattern, int? excludeRuleId, CancellationToken ct)
        => await db.ClassificationRules
            .Where(r => r.IsActive && r.JobTypeId == jobTypeId && r.Pattern == pattern
                     && (excludeRuleId == null || r.RuleId != excludeRuleId))
            .Select(r => (int?)r.RuleId)
            .FirstOrDefaultAsync(ct);
}
