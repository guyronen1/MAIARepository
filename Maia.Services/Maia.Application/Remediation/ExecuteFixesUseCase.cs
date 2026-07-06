using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Microsoft.Extensions.Logging;

namespace Maia.Application.Remediation;

public sealed class ExecuteFixesUseCase(
    IRecommendationRepository recommendations,
    IFixLogRepository fixLogs,
    IAuditRepository audit,
    IJobRepository jobs,
    IFixEngine fixEngine,
    ILogger<ExecuteFixesUseCase> logger) : IExecuteFixesUseCase
{
    /// <summary>Max recs drained per ExecuteAsync invocation. Approve endpoint
    /// + worker tick + manual /execute-fixes all share this cap; the next
    /// caller picks up remaining recs. Keeps a single drain bounded.</summary>
    private const int BatchSize = 50;

    /// <summary>How long a claim is "live" before another caller can steal it.
    /// 5 minutes is longer than any reasonable executor (Script 120s, others
    /// 60s default per executor) so a crashed worker's claim expires before
    /// a healthy worker re-attempts. Also longer than the worker's 5s poll
    /// interval so a healthy worker doesn't ever fight its own prior tick.</summary>
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        // Owner id format matches MonitoringWorker's lease id for grep-ability
        // — same shape so an operator scanning logs sees a uniform pattern.
        var claimedBy =
            $"host={Environment.MachineName};pid={Environment.ProcessId};" +
            $"runId={Guid.NewGuid():N}";

        var pending = await recommendations.ClaimPendingAsync(
            claimedBy, BatchSize, ClaimTimeout, ct);

        if (pending.Count == 0) return;

        logger.LogInformation(
            "ExecuteFixesUseCase: claimed {Count} recommendation(s) as {ClaimedBy}",
            pending.Count, claimedBy);

        foreach (var rec in pending)
        {
            ct.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Executing fix for Failure {FailureId}: [{Category}] {Action}",
                rec.FailureId, rec.FixCategory, rec.SuggestedAction);

            var outcome = await fixEngine.ExecuteAsync(rec, ct);
            var trigger = rec.OperatorApproved == true ? TriggerType.OperatorApproved : TriggerType.AutoHeal;

            // Three-way outcome from IFixEngine — drives JobStatus transition,
            // FixExecutionLog content, audit event, and logger level. Earlier
            // version branched on `rec.FixCategory == Manual` which missed the
            // (FixCategory=Retry, ActionType=Manual) case — that combo would
            // wrongly route operator-approved manual fixes to ManualRequired.
            // ActionType (carried by policy) is the truth; FixOutcome encodes it.
            var isOperatorApprovedManual = outcome.Outcome == FixOutcome.NoAutomatedAction
                                         && rec.OperatorApproved == true;

            var (logSuccess, logDetail, eventType, eventDetail, newStatus) = outcome.Outcome switch
            {
                FixOutcome.Success =>
                    (true,  "Fix applied successfully.",
                     "FixExecuted",
                     $"Executed {rec.FixCategory} fix for recommendation {rec.RecommendationId}.",
                     JobStatus.Resolved),

                // Operator approved + policy has no automated step → AwaitingManualAction
                FixOutcome.NoAutomatedAction when isOperatorApprovedManual =>
                    (true,  "Approved by operator — manual action required to complete.",
                     "ManualActionRequired",
                     $"Operator-approved manual fix for recommendation {rec.RecommendationId} — awaiting off-system action.",
                     JobStatus.AwaitingManualAction),

                // Auto-heal path hit a no-op manual policy → ManualRequired
                // (operator never weighed in; system has nothing to do; needs human review)
                FixOutcome.NoAutomatedAction =>
                    (false, "No automated action available — manual intervention required.",
                     "FixFailed",
                     $"Manual policy with no operator approval for recommendation {rec.RecommendationId}.",
                     JobStatus.ManualRequired),

                // Genuine failure — executor ran and didn't succeed. Surface the
                // executor's real reason (e.g. "Invalid column name 'updateUser'.")
                // into ResultDetail so the operator sees it in the failure drawer.
                _ =>
                    (false,
                     string.IsNullOrWhiteSpace(outcome.Detail)
                        ? "Automatic fix did not complete."
                        : $"Fix failed: {outcome.Detail}",
                     "FixFailed",
                     $"Failed {rec.FixCategory} fix for recommendation {rec.RecommendationId}: {outcome.Detail}",
                     JobStatus.ManualRequired),
            };
            var success = outcome.Outcome == FixOutcome.Success;

            await fixLogs.SaveAsync(new FixExecutionLog
            {
                FailureId        = rec.FailureId,
                RecommendationId = rec.RecommendationId,
                ExecutedAction   = rec.SuggestedAction,
                TriggerType      = trigger,
                ExecutedBy       = nameof(ExecuteFixesUseCase),
                Success          = logSuccess,
                ResultDetail     = logDetail,
                ExecutedAt       = DateTime.Now,
            }, ct);

            await audit.WriteAsync(new AuditLog
            {
                // FailureId stays populated for the (JobFailure-scoped) cascade
                // delete and the legacy join paths; EntityType/EntityId carry
                // the new generic-audit shape so an auditor can also filter
                // "everything that happened to AiRecommendation X".
                FailureId  = rec.FailureId,
                EntityType = "AiRecommendation",
                EntityId   = rec.RecommendationId.ToString(),
                EventType  = eventType,
                Actor      = nameof(ExecuteFixesUseCase),
                Detail     = eventDetail,
                Timestamp  = DateTime.Now,
            }, ct);

            await jobs.UpdateStatusAsync(rec.FailureId, newStatus, ct);

            // MarkExecutedAsync semantically means "this recommendation has
            // been actioned, stop returning it from ClaimPendingAsync". Both
            // success and operator-approved-manual qualify — the operator's
            // acknowledgement is the action for a Manual fix. Without this,
            // the next drain re-processes the rec, accumulating noise audit
            // + fix-log rows. MarkExecutedAsync also clears the claim so
            // the row's claim state stays consistent with IsExecuted.
            //
            // Failure path: explicitly ReleaseClaimAsync so the row is
            // eligible for retry by a later drain (after the claim timeout
            // would have expired anyway, but explicit-release is cleaner —
            // the next worker doesn't need to wait the full 5min for a
            // stale-claim sweep). The eligibility filter excludes failures
            // already at ManualRequired/Resolved, so genuinely-dead recs
            // don't re-loop.
            if (success || isOperatorApprovedManual)
            {
                await recommendations.MarkExecutedAsync(rec.RecommendationId, ct);
            }
            else
            {
                await recommendations.ReleaseClaimAsync(rec.RecommendationId, ct);
            }

            if (success)
            {
                logger.LogInformation("Fix executed successfully for Failure {FailureId}", rec.FailureId);
            }
            else if (isOperatorApprovedManual)
            {
                logger.LogInformation(
                    "Failure {FailureId} acknowledged by operator — awaiting manual action", rec.FailureId);
            }
            else
            {
                logger.LogWarning(
                    "Fix failed for Failure {FailureId} — requires manual intervention", rec.FailureId);
            }
        }
    }
}
