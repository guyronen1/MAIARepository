using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// FixPolicyRule CRUD, including composite policies (ordered FixPolicyRuleSteps) and
/// the two-layered default/override uniqueness guard. Split out of ConfigController;
/// see <see cref="ConfigControllerBase"/>.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = "RequireOperator")]
public class FixPolicyConfigController(
    IDbContextFactory<MaiaDbContext>   dbFactory,
    ISqlFixScopeValidator              sqlFixScope,
    IAuditRepository                   audit,
    ICurrentUserAccessor               currentUser,
    ILogger<FixPolicyConfigController> logger)
    : ConfigControllerBase(audit, currentUser, logger)
{
    /// <summary>Internal validated-step shape — keeps ValidateCompositePayload's
    /// output strongly-typed (vs reusing the DTO which has a string ActionType).</summary>
    private sealed record NormalisedStep(
        int           StepOrder,
        FixActionType ActionType,
        string        ActionPayload,
        string?       Description);

    /// <summary>
    /// Validates the composite/step shape of a FixPolicyRule upsert request.
    /// Returns true (with a populated <paramref name="error"/>) when the
    /// request is invalid — caller short-circuits with that response. Returns
    /// false (with a populated <paramref name="normalisedSteps"/>) when the
    /// request is valid; <paramref name="normalisedSteps"/> is non-empty for
    /// composite and empty/null for single-action.
    ///
    /// The eight rules enforced (in order):
    ///   1. Composite must have at least one step
    ///   2. Composite must not have a header ActionPayload
    ///   3. Non-composite must not have steps
    ///   4. No nested composite (steps cannot themselves be Composite)
    ///   5. No Manual steps (composite is automated by definition)
    ///   6. Unknown step ActionType
    ///   7. Duplicate StepOrder
    ///   8. Empty step ActionPayload
    /// </summary>
    private bool ValidateCompositePayload(
        UpsertFixPolicyRuleRequest req,
        FixActionType              actionType,
        out IActionResult          error,
        out List<NormalisedStep>?  normalisedSteps)
    {
        var hasSteps = req.Steps is { Count: > 0 };

        if (actionType == FixActionType.Composite && !hasSteps)
        {
            error = BadRequest(new { error = "CompositeRequiresSteps", message = "Composite policies require at least one step." });
            normalisedSteps = null; return true;
        }

        if (actionType == FixActionType.Composite && !string.IsNullOrWhiteSpace(req.ActionPayload))
        {
            error = BadRequest(new { error = "CompositePayloadConflict", message = "Composite policies must not set ActionPayload; payload belongs on each step." });
            normalisedSteps = null; return true;
        }

        if (actionType != FixActionType.Composite && hasSteps)
        {
            error = BadRequest(new { error = "NonCompositeWithSteps", message = "Only Composite policies can have steps. Set ActionType=Composite." });
            normalisedSteps = null; return true;
        }

        if (!hasSteps)
        {
            // Single-action policy. The one content check: a SqlScript fix is a
            // WRITE against the source DB, so it must be scoped to the failing row
            // ({sourceId} in WHERE / EXEC param) — block bulk UPDATE/DELETE.
            if (actionType == FixActionType.SqlScript
                && !string.IsNullOrWhiteSpace(req.ActionPayload)
                && sqlFixScope.Validate(req.ActionPayload!) is { } reason)
            {
                error = BadRequest(new { error = "DbFixRequiresSourceIdInWhere", message = $"SqlScript fix {reason}" });
                normalisedSteps = null; return true;
            }
            error = null!; normalisedSteps = null; return false;
        }

        var seenOrders = new HashSet<int>();
        var parsed = new List<NormalisedStep>(req.Steps!.Count);
        foreach (var s in req.Steps!)
        {
            if (!Enum.TryParse<FixActionType>(s.ActionType, out var stepActionType))
            {
                error = BadRequest(new { error = "UnknownStepActionType", message = $"Step at order {s.StepOrder} has unsupported ActionType '{s.ActionType}'." });
                normalisedSteps = null; return true;
            }
            if (stepActionType == FixActionType.Composite)
            {
                error = BadRequest(new { error = "NestedCompositeForbidden", message = $"Step at order {s.StepOrder} cannot itself be Composite." });
                normalisedSteps = null; return true;
            }
            if (stepActionType == FixActionType.Manual)
            {
                error = BadRequest(new { error = "ManualStepForbidden", message = $"Step at order {s.StepOrder} cannot be Manual. A composite is by definition automated." });
                normalisedSteps = null; return true;
            }
            if (string.IsNullOrWhiteSpace(s.ActionPayload))
            {
                error = BadRequest(new { error = "StepPayloadRequired", message = $"Step at order {s.StepOrder} has no payload." });
                normalisedSteps = null; return true;
            }
            // Same WRITE guard as single-action, per SqlScript step.
            if (stepActionType == FixActionType.SqlScript
                && sqlFixScope.Validate(s.ActionPayload!) is { } stepReason)
            {
                error = BadRequest(new { error = "DbFixRequiresSourceIdInWhere", message = $"SqlScript step at order {s.StepOrder} {stepReason}" });
                normalisedSteps = null; return true;
            }
            if (!seenOrders.Add(s.StepOrder))
            {
                error = BadRequest(new { error = "DuplicateStepOrder", message = $"Step orders must be unique within a policy (duplicate: {s.StepOrder})." });
                normalisedSteps = null; return true;
            }
            parsed.Add(new NormalisedStep(s.StepOrder, stepActionType, s.ActionPayload, s.Description));
        }

        // Renormalise orders to 1..N regardless of input — spares the UI from
        // having to re-pack after deletes. Sort by input StepOrder ascending
        // to preserve the operator's intended sequence.
        var packed = parsed
            .OrderBy(p => p.StepOrder)
            .Select((p, i) => p with { StepOrder = i + 1 })
            .ToList();

        error = null!; normalisedSteps = packed; return false;
    }

    /// <summary>Compact one-line summary of a step list for audit diff text.
    /// Order matters for diff legibility: a reorder shows clearly here.</summary>
    private static string SummariseSteps(IEnumerable<FixPolicyRuleStep> steps)
    {
        var ordered = steps.OrderBy(s => s.StepOrder).ToList();
        return ordered.Count == 0
            ? "(none)"
            : string.Join(" → ", ordered.Select(s => $"{s.StepOrder}:{s.ActionType}"));
    }

    [HttpGet("fix-policy-rules")]
    public async Task<IActionResult> GetFixPolicyRules(
        [FromQuery] int? jobTypeId,
        [FromQuery] int? monitoredJobId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Steps include is split so EF doesn't Cartesian-join ErrorType ×
        // Steps when both are pulled in the same query.
        var q = db.FixPolicyRules
            .Include(r => r.ErrorType)
            .Include(r => r.Steps.OrderBy(s => s.StepOrder))
            .AsSplitQuery()
            .Where(r => r.Enabled);

        // Filtering semantics:
        //   monitoredJobId set → returns defaults for the job's JobType
        //                        PLUS any override scoped to that job.
        //                        Lets the per-job Fix Options tab show the
        //                        full effective config for that job.
        //   jobTypeId only     → returns every enabled rule under the JobType
        //                        (defaults + all overrides for any of its jobs).
        //   neither            → returns every enabled rule.
        if (monitoredJobId.HasValue)
        {
            q = q.Where(r =>
                (r.MonitoredJobId == monitoredJobId.Value)
             || (r.MonitoredJobId == null && jobTypeId.HasValue && r.JobTypeId == jobTypeId.Value));
        }
        else if (jobTypeId.HasValue)
        {
            q = q.Where(r => r.JobTypeId == jobTypeId.Value);
        }

        var rules = await q.OrderBy(r => r.ErrorTypeId).ThenBy(r => r.MonitoredJobId).ToListAsync(ct);
        return Ok(rules.Select(r => new
        {
            r.RuleId, r.JobTypeId, r.ErrorTypeId, r.MonitoredJobId,
            ErrorTypeCode      = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.ActionToApply,
            FixCategory        = r.FixCategory.ToString(),
            ActionType         = r.ActionType.ToString(),
            r.ActionPayload, r.IsAutoHealEligible, r.Enabled,
            // Empty array (not null) for non-composite — cleaner on the wire.
            Steps              = r.Steps
                .OrderBy(s => s.StepOrder)
                .Select(s => new { s.StepId, s.StepOrder, ActionType = s.ActionType.ToString(), s.ActionPayload, s.Description })
                .ToList(),
        }));
    }

    [HttpGet("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> GetFixPolicyRule(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var r = await db.FixPolicyRules
            .Include(p => p.ErrorType)
            .Include(p => p.Steps.OrderBy(s => s.StepOrder))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.RuleId == id, ct);
        if (r is null) return NotFound();
        return Ok(new
        {
            r.RuleId, r.JobTypeId, r.ErrorTypeId, r.MonitoredJobId,
            ErrorTypeCode = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.ActionToApply,
            FixCategory   = r.FixCategory.ToString(),
            ActionType    = r.ActionType.ToString(),
            r.ActionPayload, r.IsAutoHealEligible, r.Enabled,
            Steps         = r.Steps
                .OrderBy(s => s.StepOrder)
                .Select(s => new { s.StepId, s.StepOrder, ActionType = s.ActionType.ToString(), s.ActionPayload, s.Description })
                .ToList(),
        });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("fix-policy-rules")]
    public async Task<IActionResult> CreateFixPolicyRule([FromBody] UpsertFixPolicyRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!Enum.TryParse<FixCategory>(req.FixCategory, out var fixCategory))
            return BadRequest($"Unknown FixCategory: '{req.FixCategory}'");
        if (!Enum.TryParse<FixActionType>(req.ActionType, out var actionType))
            return BadRequest($"Unknown ActionType: '{req.ActionType}'");

        // Composite-shape validation. Same rules apply to Create and Update;
        // see ValidateCompositePayload for the eight individual checks.
        if (ValidateCompositePayload(req, actionType, out var validationError, out var validatedSteps))
            return validationError;

        // Two-pronged duplicate check — different layer, different key:
        //   • Override layer: at most one enabled rule per (MonitoredJobId, ErrorTypeId)
        //   • Default  layer: at most one enabled rule per (JobTypeId, ErrorTypeId) AND MonitoredJobId IS NULL
        // A default + an override for the same (JobType, ErrorType) are NOT
        // duplicates — they're complementary by design. The filtered unique
        // indexes on the DB enforce the same two-pronged constraint as the
        // last line of defence.
        if (req.Enabled)
        {
            var conflict = req.MonitoredJobId is int monId
                ? await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.MonitoredJobId == monId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.Enabled, ct)
                : await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.JobTypeId      == req.JobTypeId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.MonitoredJobId == null
                      && p.Enabled, ct);
            if (conflict is not null)
            {
                return Conflict(new
                {
                    error               = "DuplicateFixPolicy",
                    message             = req.MonitoredJobId.HasValue
                        ? "An active fix policy override already exists for this job + Error Type combination. Disable the existing override or edit it instead of creating a new one."
                        : "An active fix policy already exists for this Job Type + Error Type combination. Disable the existing policy or edit it instead of creating a new one.",
                    conflictingPolicyId = conflict.RuleId,
                });
            }
        }

        var rule = new FixPolicyRule
        {
            JobTypeId          = req.JobTypeId,
            ErrorTypeId        = req.ErrorTypeId,
            MonitoredJobId     = req.MonitoredJobId,
            ActionToApply      = req.ActionToApply,
            FixCategory        = fixCategory,
            ActionType         = actionType,
            // Composite header has no payload of its own (validated above).
            ActionPayload      = actionType == FixActionType.Composite ? null : req.ActionPayload,
            IsAutoHealEligible = req.IsAutoHealEligible,
            Enabled            = req.Enabled,
            CreatedBy          = Actor,
            ActionTimestamp    = DateTime.Now,
            // Provenance when created from an /unconfigured Case-B gap (else null).
            SuggestedBy         = req.SuggestedBy,
            SuggestedFromHash   = req.SuggestedFromHash,
            SuggestedConfidence = req.SuggestedConfidence,
        };
        db.FixPolicyRules.Add(rule);
        await db.SaveChangesAsync(ct);

        // Steps persistence — validatedSteps is the normalised list (orders
        // packed 1..N). Empty for non-composite policies.
        if (validatedSteps is { Count: > 0 })
        {
            foreach (var s in validatedSteps)
                db.FixPolicyRuleSteps.Add(new FixPolicyRuleStep
                {
                    RuleId        = rule.RuleId,
                    StepOrder     = s.StepOrder,
                    ActionType    = s.ActionType,
                    ActionPayload = s.ActionPayload,
                    Description   = s.Description,
                });
            await db.SaveChangesAsync(ct);
        }

        var stepSummary = validatedSteps is { Count: > 0 }
            ? $", {validatedSteps.Count} steps: {string.Join(", ", validatedSteps.Select(s => s.ActionType))}"
            : string.Empty;

        await WriteAuditAsync(
            entityType: "FixPolicyRule",
            entityId:   rule.RuleId.ToString(),
            eventType:  "FixPolicyCreated",
            actor:      Actor,
            detail:     $"Created FixPolicyRule (JobTypeId={rule.JobTypeId}, ErrorTypeId={rule.ErrorTypeId}, MonitoredJobId={FormatValue((object?)rule.MonitoredJobId)}, FixCategory={rule.FixCategory}, ActionType={rule.ActionType}, IsAutoHealEligible={FormatValue(rule.IsAutoHealEligible)}, Enabled={FormatValue(rule.Enabled)}{stepSummary})",
            ct: ct);

        return Ok(new { rule.RuleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> UpdateFixPolicyRule(int id, [FromBody] UpsertFixPolicyRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.FixPolicyRules
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.RuleId == id, ct);
        if (rule is null) return NotFound();
        if (!Enum.TryParse<FixCategory>(req.FixCategory, out var fixCat))
            return BadRequest($"Unknown FixCategory: '{req.FixCategory}'");
        if (!Enum.TryParse<FixActionType>(req.ActionType, out var actType))
            return BadRequest($"Unknown ActionType: '{req.ActionType}'");

        // Composite-shape validation — same rules as Create.
        if (ValidateCompositePayload(req, actType, out var validationError, out var validatedSteps))
            return validationError;

        // Two-pronged duplicate guard, same as Create but excluding self.
        // The req.MonitoredJobId on the PUT determines which layer the
        // resulting row will live in — switching layers via edit is allowed,
        // but must not produce two enabled rows at the same key.
        if (req.Enabled)
        {
            var conflict = req.MonitoredJobId is int monId
                ? await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.RuleId         != id
                      && p.MonitoredJobId == monId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.Enabled, ct)
                : await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.RuleId         != id
                      && p.JobTypeId      == req.JobTypeId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.MonitoredJobId == null
                      && p.Enabled, ct);
            if (conflict is not null)
            {
                return Conflict(new
                {
                    error               = "DuplicateFixPolicy",
                    message             = req.MonitoredJobId.HasValue
                        ? "An active fix policy override already exists for this job + Error Type combination. Disable the existing override or edit it instead of creating a new one."
                        : "An active fix policy already exists for this Job Type + Error Type combination. Disable the existing policy or edit it instead of creating a new one.",
                    conflictingPolicyId = conflict.RuleId,
                });
            }
        }

        // IsAutoHealEligible flips are the highest-stakes config change in
        // the whole system — an auto-heal toggle decides whether a future
        // recommendation runs without human review. Capture every field.
        // MonitoredJobId is captured too — flipping a default into an
        // override (or vice versa) is a scope change worth auditing.
        var beforeErrorTypeId    = rule.ErrorTypeId;
        var beforeMonitoredJobId = rule.MonitoredJobId;
        var beforeActionToApply  = rule.ActionToApply;
        var beforeFixCategory    = rule.FixCategory;
        var beforeActionType     = rule.ActionType;
        var beforeActionPayload  = rule.ActionPayload;
        var beforeIsAutoHeal     = rule.IsAutoHealEligible;
        var beforeEnabled        = rule.Enabled;

        // Snapshot prior step shape for the diff (operator-visible summary).
        var beforeStepShape = SummariseSteps(rule.Steps);

        rule.ErrorTypeId        = req.ErrorTypeId;
        rule.MonitoredJobId     = req.MonitoredJobId;
        rule.ActionToApply      = req.ActionToApply;
        rule.FixCategory        = fixCat;
        rule.ActionType         = actType;
        rule.ActionPayload      = actType == FixActionType.Composite ? null : req.ActionPayload;
        rule.IsAutoHealEligible = req.IsAutoHealEligible;
        rule.Enabled            = req.Enabled;

        // Steps update: replace-all. Diffing would be cleaner per-step but
        // steps are small, replace-all keeps the code simple and the cascade
        // FK handles orphan deletes for us. Validated steps already have
        // packed orders 1..N.
        db.FixPolicyRuleSteps.RemoveRange(rule.Steps);
        if (validatedSteps is { Count: > 0 })
        {
            foreach (var s in validatedSteps)
                db.FixPolicyRuleSteps.Add(new FixPolicyRuleStep
                {
                    RuleId        = rule.RuleId,
                    StepOrder     = s.StepOrder,
                    ActionType    = s.ActionType,
                    ActionPayload = s.ActionPayload,
                    Description   = s.Description,
                });
        }

        await db.SaveChangesAsync(ct);

        var afterStepShape = SummariseSteps(rule.Steps);
        var diff = BuildDiff(
            ("ErrorTypeId",        beforeErrorTypeId,    rule.ErrorTypeId),
            ("MonitoredJobId",     (object?)beforeMonitoredJobId, (object?)rule.MonitoredJobId),
            ("ActionToApply",      beforeActionToApply,  rule.ActionToApply),
            ("FixCategory",        beforeFixCategory,    rule.FixCategory),
            ("ActionType",         beforeActionType,     rule.ActionType),
            ("ActionPayload",      beforeActionPayload,  rule.ActionPayload),
            ("IsAutoHealEligible", beforeIsAutoHeal,     rule.IsAutoHealEligible),
            ("Enabled",            beforeEnabled,        rule.Enabled),
            ("Steps",              beforeStepShape,      afterStepShape));
        await WriteAuditAsync(
            entityType: "FixPolicyRule",
            entityId:   id.ToString(),
            eventType:  "FixPolicyUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> DeleteFixPolicyRule(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.FixPolicyRules.FindAsync([id], ct);
        if (rule is null) return NotFound();
        var snapshotJobTypeId   = rule.JobTypeId;
        var snapshotErrorTypeId = rule.ErrorTypeId;
        var snapshotActionType  = rule.ActionType;
        rule.Enabled = false;
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "FixPolicyRule",
            entityId:   id.ToString(),
            eventType:  "FixPolicyDeleted",
            actor:      Actor,
            detail:     $"Soft-deleted FixPolicyRule {id} (JobTypeId={snapshotJobTypeId}, ErrorTypeId={snapshotErrorTypeId}, ActionType={snapshotActionType})",
            ct: ct);

        return NoContent();
    }
}
