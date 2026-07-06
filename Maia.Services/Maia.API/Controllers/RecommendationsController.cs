using Maia.API.Contracts;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// Operator decisions on AI recommendations. Approve flips
/// <see cref="AiRecommendation.OperatorApproved"/> to true and synchronously drains the
/// pending fix queue so the fix runs on the same request. Reject sets it to false and
/// — when no other pending recs remain on a still-Failed failure — flips the failure
/// to <see cref="JobStatus.ManualRequired"/> so the operator's decline is visible in
/// the status badge + stage pipeline (otherwise the failure would look "stuck on
/// Recommended" forever).
///
/// Both endpoints record an <see cref="OperatorAction"/> and an <see cref="AuditLog"/> entry.
/// </summary>
[ApiController]
[Route("api/recommendations")]
[Authorize(Policy = "RequireOperator")]   // operator decisions on recommendations
public class RecommendationsController(
    IRecommendationRepository       recommendations,
    IOperatorActionRepository       operatorActions,
    IAuditRepository                audit,
    IJobRepository                  jobs,
    IExecuteFixesUseCase            execute,
    IDbContextFactory<MaiaDbContext>  dbFactory,
    ICurrentUserAccessor            currentUser) : ControllerBase
{
    // Actor is the authenticated principal — guaranteed present here because the
    // controller requires RequireOperator (no anonymous reaches the action body).
    private string Actor => currentUser.UserName!;

    [HttpPost("{id:int}/approve")]
    public Task<IActionResult> Approve(int id, CancellationToken ct)
        => RecordDecisionAsync(id, approved: true, ct);

    [HttpPost("{id:int}/reject")]
    public Task<IActionResult> Reject(int id, CancellationToken ct)
        => RecordDecisionAsync(id, approved: false, ct);

    /// <summary>
    /// Re-runs a fix that previously failed to execute. A failed executor
    /// leaves the failure in <see cref="JobStatus.ManualRequired"/>, and the
    /// drain's claim guard (<c>Failure.Status == Failed</c>) deliberately keeps
    /// it from auto-retrying forever. This endpoint is the explicit operator
    /// override for "I fixed the root cause (e.g. corrected the policy SQL) —
    /// try the same failure again": it re-arms the recommendation + failure and
    /// synchronously drains, so the fix re-runs with whatever policy is
    /// configured NOW. Only valid while the failure is in ManualRequired.
    /// </summary>
    [HttpPost("{id:int}/retry")]
    public async Task<IActionResult> Retry(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rec = await db.AIRecommendations
            .Include(r => r.Failure)
            .FirstOrDefaultAsync(r => r.RecommendationId == id, ct);

        if (rec is null)
            return NotFound(new { Message = $"Recommendation {id} not found." });
        if (rec.Failure is null)
            return NotFound(new { Message = $"Failure for recommendation {id} not found." });

        // Retry only makes sense for a fix that failed to execute — i.e. the
        // failure is sitting in ManualRequired. Block other states so we don't
        // re-run a fix on an already-Resolved failure or one mid-flight.
        if (rec.Failure.Status != JobStatus.ManualRequired)
            return Conflict(new
            {
                error   = "RetryNotApplicable",
                Message = $"Retry only applies to failures in ManualRequired (current: {rec.Failure.Status}).",
            });

        // Re-arm: clear the executed flag + any stale claim, approve so it's
        // eligible regardless of AutoFixAvailable, and move the failure back to
        // Failed so the drain's claim guard lets it through. Persist BEFORE the
        // drain so the use case's fresh query sees the re-armed row.
        rec.IsExecuted       = false;
        rec.OperatorApproved = true;
        rec.ClaimedBy        = null;
        rec.ClaimedAt        = null;
        rec.Failure.Status   = JobStatus.Failed;
        await db.SaveChangesAsync(ct);

        var actor = Actor;
        await operatorActions.SaveAsync(new OperatorAction
        {
            RecommendationId = id,
            OperatorId       = actor,
            ActionTaken      = "Retry",
            ActionTimestamp  = DateTime.Now,
        }, ct);

        await audit.WriteAsync(new AuditLog
        {
            FailureId  = rec.FailureId,
            EntityType = "AiRecommendation",
            EntityId   = id.ToString(),
            EventType  = "FixRetried",
            Actor      = actor,
            Detail     = $"Operator {actor} retried recommendation {id} — failure re-armed " +
                         $"from ManualRequired to Failed and re-queued for execution (action: {rec.SuggestedAction}).",
            Timestamp  = DateTime.Now,
        }, ct);

        // Synchronous drain — re-runs the fix on this request, same as approve.
        await execute.ExecuteAsync(ct);

        // Fresh read so the response reflects the post-drain state (Resolved if
        // the fix now works, ManualRequired again if it still fails).
        await using var db2 = await dbFactory.CreateDbContextAsync(ct);
        var after = await db2.AIRecommendations
            .Include(r => r.ErrorType)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RecommendationId == id, ct);

        return Ok(after is null ? (object)new { Message = "Retried." } : RecommendationDto.From(after));
    }

    private async Task<IActionResult> RecordDecisionAsync(
        int id, bool approved, CancellationToken ct)
    {
        // Need FailureId for the audit row, so fetch before mutating
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rec = await db.AIRecommendations
            .Include(r => r.ErrorType)
            .FirstOrDefaultAsync(r => r.RecommendationId == id, ct);

        if (rec is null)
            return NotFound(new { Message = $"Recommendation {id} not found." });

        var updated = await recommendations.SetApprovalAsync(id, approved, ct);
        if (!updated)
            return NotFound(new { Message = $"Recommendation {id} not found." });

        var actor       = Actor;
        var actionTaken = approved ? "Approve" : "Reject";
        await operatorActions.SaveAsync(new OperatorAction
        {
            RecommendationId = id,
            OperatorId       = actor,
            ActionTaken      = actionTaken,
            ActionTimestamp  = DateTime.Now,
        }, ct);

        await audit.WriteAsync(new AuditLog
        {
            // Populate both the legacy FailureId FK and the generic
            // EntityType/EntityId discriminator so this row shows up in
            // either query path. Same shape ExecuteFixesUseCase now writes.
            FailureId  = rec.FailureId,
            EntityType = "AiRecommendation",
            EntityId   = id.ToString(),
            EventType  = approved ? "OperatorApproved" : "OperatorRejected",
            Actor      = actor,
            Detail     = $"Operator {actor} {actionTaken.ToLowerInvariant()}d recommendation {id} " +
                         $"(action: {rec.SuggestedAction}).",
            Timestamp  = DateTime.Now,
        }, ct);

        if (approved)
        {
            await execute.ExecuteAsync(ct);
        }
        else
        {
            // Rejection of the LAST pending rec on a Failed failure → flip
            // the failure to ManualRequired so operator's decision is visible
            // in the status badge + stage pipeline. Skip if:
            //   - another rec on the same failure is still pending (the
            //     operator hasn't decided everything)
            //   - the failure is already past the Failed state (e.g.
            //     AwaitingManualAction because a sibling rec was approved)
            await TransitionFailureIfLastRejectionAsync(rec.FailureId, id, db, actor, ct);
        }

        rec.OperatorApproved = approved;
        return Ok(RecommendationDto.From(rec));
    }

    /// <summary>
    /// Idempotent: only flips Status when (a) failure is still Failed and
    /// (b) no recs on the failure are pending (OperatorApproved IS NULL AND
    /// IsExecuted = 0). Excludes the just-rejected rec from the pending
    /// count (it was just rejected, the SetApprovalAsync write may not have
    /// propagated to this query session depending on timing — explicit
    /// exclusion is safer than relying on read-after-write).
    /// </summary>
    private async Task TransitionFailureIfLastRejectionAsync(
        int failureId, int justRejectedId, MaiaDbContext db, string operatorId, CancellationToken ct)
    {
        var failure = await db.JobFailures.FirstOrDefaultAsync(f => f.FailureId == failureId, ct);
        if (failure is null || failure.Status != JobStatus.Failed) return;

        var otherPending = await db.AIRecommendations.AnyAsync(
            r => r.FailureId == failureId
              && r.RecommendationId != justRejectedId
              && r.OperatorApproved == null
              && !r.IsExecuted, ct);
        if (otherPending) return;

        await jobs.UpdateStatusAsync(failureId, JobStatus.ManualRequired, ct);

        await audit.WriteAsync(new AuditLog
        {
            FailureId  = failureId,
            EntityType = "JobFailure",
            EntityId   = failureId.ToString(),
            EventType  = "ManualActionRequired",
            Actor      = operatorId,
            Detail     = $"Operator {operatorId} rejected the last pending recommendation — failure transitioned to ManualRequired.",
            Timestamp  = DateTime.Now,
        }, ct);
    }
}
