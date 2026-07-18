using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

public sealed class DbFixHandler(ILogger<DbFixHandler> logger) : IFixHandler
{
    public FixCategory Category => FixCategory.DbFix;

    public async Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        // This category fallback is only reached when NO FixPolicyRule is configured
        // for the failure's (JobType, ErrorType) — there is no built-in automated
        // "DB connectivity" remediation. Rather than silently pretend to attempt one,
        // fail honestly with an actionable message. Returning false routes the failure
        // to ManualRequired (see ExecuteFixesUseCase), which is the correct outcome:
        // an operator either fixes the DB out-of-band, or configures a FixPolicyRule
        // (SqlScript / StoredProcedure) for this error type so future occurrences
        // auto-remediate. When a real DbFix remediation is built, it should reuse
        // ISqlFixScopeValidator for any write it performs.
        logger.LogWarning(
            "No automated DbFix handler for Failure {FailureId} (ErrorTypeId {ErrorTypeId}) — " +
            "no FixPolicyRule is configured for this (JobType, ErrorType). Routing to ManualRequired. " +
            "Configure a SqlScript/StoredProcedure fix policy to auto-remediate this error type.",
            recommendation.FailureId, recommendation.ErrorTypeId);

        await Task.CompletedTask;
        return false;
    }
}
