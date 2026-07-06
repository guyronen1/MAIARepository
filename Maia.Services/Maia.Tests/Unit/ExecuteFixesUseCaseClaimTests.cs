using Maia.Application.Remediation;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Covers the claim/release contract introduced to fix the double-execution
/// race. The atomic UPDATE itself is verified at the SQL Server level (live
/// integration); these tests verify the use case CALLS the right repo
/// methods on success / failure / no-claim paths.
/// </summary>
public class ExecuteFixesUseCaseClaimTests
{
    private readonly Mock<IRecommendationRepository> _recRepo  = new();
    private readonly Mock<IFixLogRepository>         _fixLogs  = new();
    private readonly Mock<IAuditRepository>          _audit    = new();
    private readonly Mock<IJobRepository>            _jobs     = new();
    private readonly Mock<IFixEngine>                _engine   = new();

    public ExecuteFixesUseCaseClaimTests()
    {
        // Default: claim returns nothing — overridden per-test.
        _recRepo.Setup(r => r.ClaimPendingAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AiRecommendation>());

        _fixLogs.Setup(r => r.SaveAsync(It.IsAny<FixExecutionLog>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _audit.Setup(r => r.WriteAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jobs.Setup(r => r.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<JobStatus>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_NoClaims_DoesNothing()
    {
        await CreateSut().ExecuteAsync();

        _engine.Verify(e => e.ExecuteAsync(It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()), Times.Never);
        _recRepo.Verify(r => r.MarkExecutedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _recRepo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ClaimsRecsWithExpectedBatchAndTimeout()
    {
        await CreateSut().ExecuteAsync();

        // Verifies the use case picks reasonable values for batch + timeout.
        // 50 batch + 5min timeout are documented choices in CLAUDE.md; this
        // test fails loudly if someone changes them without thought.
        _recRepo.Verify(r => r.ClaimPendingAsync(
            It.Is<string>(s => s.Contains("host=") && s.Contains("pid=") && s.Contains("runId=")),
            50,
            TimeSpan.FromMinutes(5),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_SuccessfulFix_CallsMarkExecutedAndNotReleaseClaim()
    {
        var rec = MakeRec(failureId: 1);
        _recRepo.Setup(r => r.ClaimPendingAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AiRecommendation> { rec });
        _engine.Setup(e => e.ExecuteAsync(rec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixResult(FixOutcome.Success));

        await CreateSut().ExecuteAsync();

        _recRepo.Verify(r => r.MarkExecutedAsync(rec.RecommendationId, It.IsAny<CancellationToken>()), Times.Once);
        _recRepo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_FailedFix_CallsReleaseClaimAndNotMarkExecuted()
    {
        // Failure path: the rec stays !IsExecuted but the claim is cleared
        // so a later drain (after the failure status check decides) can pick
        // it up. The eligibility filter excludes failures already at
        // ManualRequired so genuinely-dead recs don't re-loop.
        var rec = MakeRec(failureId: 2);
        _recRepo.Setup(r => r.ClaimPendingAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AiRecommendation> { rec });
        _engine.Setup(e => e.ExecuteAsync(rec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixResult(FixOutcome.Failed));

        await CreateSut().ExecuteAsync();

        _recRepo.Verify(r => r.ReleaseClaimAsync(rec.RecommendationId, It.IsAny<CancellationToken>()), Times.Once);
        _recRepo.Verify(r => r.MarkExecutedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_OperatorApprovedManual_MarksExecutedNotReleaseClaim()
    {
        // NoAutomatedAction + OperatorApproved=true → operator acknowledged a
        // Manual rec; treat as "actioned, stop offering it" → MarkExecuted.
        // Same MarkExecuted call also clears the claim (atomic).
        var rec = MakeRec(failureId: 3);
        rec.OperatorApproved = true;
        rec.FixCategory      = FixCategory.Manual;
        _recRepo.Setup(r => r.ClaimPendingAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AiRecommendation> { rec });
        _engine.Setup(e => e.ExecuteAsync(rec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixResult(FixOutcome.NoAutomatedAction));

        await CreateSut().ExecuteAsync();

        _recRepo.Verify(r => r.MarkExecutedAsync(rec.RecommendationId, It.IsAny<CancellationToken>()), Times.Once);
        _recRepo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_AutoHealNoAutomatedAction_ReleaseClaimNotMarkExecuted()
    {
        // NoAutomatedAction without OperatorApproved → auto-heal hit a Manual
        // policy with no operator weighing in. Routed to ManualRequired by
        // the use case (status update); claim released so a future operator
        // approval can re-trigger. The eligibility filter on f.Status=Failed
        // excludes it from re-claim after the status moves to ManualRequired,
        // so this is safe — the release just leaves the row in a clean state.
        var rec = MakeRec(failureId: 4);
        // OperatorApproved is null by default (not approved, not rejected)
        _recRepo.Setup(r => r.ClaimPendingAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AiRecommendation> { rec });
        _engine.Setup(e => e.ExecuteAsync(rec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixResult(FixOutcome.NoAutomatedAction));

        await CreateSut().ExecuteAsync();

        _recRepo.Verify(r => r.ReleaseClaimAsync(rec.RecommendationId, It.IsAny<CancellationToken>()), Times.Once);
        _recRepo.Verify(r => r.MarkExecutedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private ExecuteFixesUseCase CreateSut() => new(
        _recRepo.Object, _fixLogs.Object, _audit.Object,
        _jobs.Object, _engine.Object,
        NullLogger<ExecuteFixesUseCase>.Instance);

    private static AiRecommendation MakeRec(int failureId) => new()
    {
        RecommendationId = failureId * 10,        // distinct from FailureId
        FailureId        = failureId,
        ErrorTypeId      = 1,
        SuggestedAction  = "test",
        FixCategory      = FixCategory.Retry,
        ConfidenceScore  = 0.9m,
    };
}
