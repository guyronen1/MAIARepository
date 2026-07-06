using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Covers DefaultFixEngine's inline Composite orchestration: best-effort
/// execution, per-step FixExecutionLog, and overall outcome semantics
/// (any step fails → FixOutcome.Failed, all succeed → Success).
/// </summary>
public class DefaultFixEngineCompositeTests
{
    private readonly Mock<IFixPolicyRepository> _policyRepo = new();
    private readonly Mock<IFixLogRepository>    _fixLogs    = new();

    [Fact]
    public async Task Execute_AllStepsSucceed_ReturnsSuccessAndLogsEachStep()
    {
        var policy = MakeCompositePolicy(
            ruleId: 100,
            steps: [
                MakeStep(1, FixActionType.SqlScript, "UPDATE ..."),
                MakeStep(2, FixActionType.Script,    "powershell foo.ps1"),
                MakeStep(3, FixActionType.SqlScript, "DELETE ..."),
            ]);
        _policyRepo.Setup(r => r.GetForAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        // Three step executors: all return true.
        var executor = StubExecutor(FixActionType.SqlScript, returns: true);
        var script   = StubExecutor(FixActionType.Script,    returns: true);
        var logged   = new List<FixExecutionLog>();
        _fixLogs.Setup(r => r.SaveAsync(It.IsAny<FixExecutionLog>(), It.IsAny<CancellationToken>()))
            .Callback<FixExecutionLog, CancellationToken>((log, _) => logged.Add(log))
            .Returns(Task.CompletedTask);

        var engine = new DefaultFixEngine(
            _policyRepo.Object,
            [executor.Object, script.Object],
            [],
            _fixLogs.Object,
            NullLogger<DefaultFixEngine>.Instance);

        var rec = MakeRec(failureId: 7, errorTypeId: 1, jobTypeId: 1);
        var outcome = await engine.ExecuteAsync(rec);

        Assert.Equal(FixOutcome.Success, outcome.Outcome);
        Assert.Equal(3, logged.Count);
        Assert.All(logged, log => Assert.True(log.Success));
    }

    [Fact]
    public async Task Execute_MiddleStepFails_RemainingStepsStillRunAndOverallFails()
    {
        // Best-effort semantics: step 2 fails, step 3 must still run, overall = Failed.
        var sqlExecutor    = new Mock<IFixActionExecutor>();
        var scriptExecutor = new Mock<IFixActionExecutor>();
        sqlExecutor.SetupGet(e => e.ActionType).Returns(FixActionType.SqlScript);
        scriptExecutor.SetupGet(e => e.ActionType).Returns(FixActionType.Script);

        var executedActions = new List<FixActionType>();
        sqlExecutor.Setup(e => e.ExecuteAsync(It.IsAny<string?>(), It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()))
            .Callback(() => executedActions.Add(FixActionType.SqlScript))
            .ReturnsAsync(FixActionResult.Ok());
        scriptExecutor.Setup(e => e.ExecuteAsync(It.IsAny<string?>(), It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()))
            .Callback(() => executedActions.Add(FixActionType.Script))
            .ReturnsAsync(FixActionResult.Fail("step failed"));   // ← the failing step

        var policy = MakeCompositePolicy(
            ruleId: 200,
            steps: [
                MakeStep(1, FixActionType.SqlScript, "step 1"),
                MakeStep(2, FixActionType.Script,    "step 2"),       // will fail
                MakeStep(3, FixActionType.SqlScript, "step 3"),
            ]);
        _policyRepo.Setup(r => r.GetForAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        var logged = new List<FixExecutionLog>();
        _fixLogs.Setup(r => r.SaveAsync(It.IsAny<FixExecutionLog>(), It.IsAny<CancellationToken>()))
            .Callback<FixExecutionLog, CancellationToken>((log, _) => logged.Add(log))
            .Returns(Task.CompletedTask);

        var engine = new DefaultFixEngine(
            _policyRepo.Object,
            [sqlExecutor.Object, scriptExecutor.Object],
            [],
            _fixLogs.Object,
            NullLogger<DefaultFixEngine>.Instance);

        var outcome = await engine.ExecuteAsync(MakeRec(7, 1, 1));

        Assert.Equal(FixOutcome.Failed, outcome.Outcome);
        // All three steps ran in order — best-effort, no abort.
        Assert.Equal(
            new[] { FixActionType.SqlScript, FixActionType.Script, FixActionType.SqlScript },
            executedActions);
        // Per-step log: 2 successes + 1 failure.
        Assert.Equal(3, logged.Count);
        Assert.Equal(new[] { true, false, true }, logged.Select(l => l.Success));
    }

    [Fact]
    public async Task Execute_StepsRunInStepOrderRegardlessOfInsertionOrder()
    {
        // Steps added with shuffled StepOrder values; engine must order them
        // ascending so the executor sees 1 → 2 → 3.
        var policy = MakeCompositePolicy(
            ruleId: 300,
            steps: [
                MakeStep(3, FixActionType.SqlScript, "third"),
                MakeStep(1, FixActionType.SqlScript, "first"),
                MakeStep(2, FixActionType.SqlScript, "second"),
            ]);
        _policyRepo.Setup(r => r.GetForAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        var executor = new Mock<IFixActionExecutor>();
        executor.SetupGet(e => e.ActionType).Returns(FixActionType.SqlScript);
        var payloadsExecuted = new List<string?>();
        executor.Setup(e => e.ExecuteAsync(It.IsAny<string?>(), It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()))
            .Callback<string?, AiRecommendation, CancellationToken>((payload, _, _) => payloadsExecuted.Add(payload))
            .ReturnsAsync(FixActionResult.Ok());

        _fixLogs.Setup(r => r.SaveAsync(It.IsAny<FixExecutionLog>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var engine = new DefaultFixEngine(
            _policyRepo.Object, [executor.Object], [], _fixLogs.Object,
            NullLogger<DefaultFixEngine>.Instance);

        await engine.ExecuteAsync(MakeRec(7, 1, 1));

        Assert.Equal(new[] { "first", "second", "third" }, payloadsExecuted);
    }

    [Fact]
    public async Task Execute_EmptySteps_ReturnsFailed()
    {
        // Defensive: controller validation prevents this, but a hand-edited
        // DB row could have ActionType=Composite + Steps={}. Engine treats
        // as misconfiguration → Failed (NOT NoAutomatedAction, because the
        // operator clearly intended an automated path).
        var policy = MakeCompositePolicy(ruleId: 400, steps: []);
        _policyRepo.Setup(r => r.GetForAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        var engine = new DefaultFixEngine(
            _policyRepo.Object, [], [], _fixLogs.Object,
            NullLogger<DefaultFixEngine>.Instance);

        var outcome = await engine.ExecuteAsync(MakeRec(7, 1, 1));

        Assert.Equal(FixOutcome.Failed, outcome.Outcome);
    }

    [Fact]
    public async Task Execute_NoExecutorForStepActionType_StepLogsAsFailure()
    {
        // Step asks for CopyFile but DI didn't register one. Step gets a
        // failure log row; remaining steps still run; overall = Failed.
        var policy = MakeCompositePolicy(
            ruleId: 500,
            steps: [
                MakeStep(1, FixActionType.CopyFile,  "src|dst"),    // no executor
                MakeStep(2, FixActionType.SqlScript, "UPDATE ..."),
            ]);
        _policyRepo.Setup(r => r.GetForAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        var sql = StubExecutor(FixActionType.SqlScript, returns: true);
        var logged = new List<FixExecutionLog>();
        _fixLogs.Setup(r => r.SaveAsync(It.IsAny<FixExecutionLog>(), It.IsAny<CancellationToken>()))
            .Callback<FixExecutionLog, CancellationToken>((log, _) => logged.Add(log))
            .Returns(Task.CompletedTask);

        var engine = new DefaultFixEngine(
            _policyRepo.Object, [sql.Object], [], _fixLogs.Object,
            NullLogger<DefaultFixEngine>.Instance);

        var outcome = await engine.ExecuteAsync(MakeRec(7, 1, 1));

        Assert.Equal(FixOutcome.Failed, outcome.Outcome);
        Assert.Equal(2, logged.Count);
        Assert.False(logged[0].Success);                // missing executor
        Assert.Contains("no executor", logged[0].ResultDetail, StringComparison.OrdinalIgnoreCase);
        Assert.True(logged[1].Success);                 // SqlScript ran fine
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static FixPolicyRule MakeCompositePolicy(int ruleId, IList<FixPolicyRuleStep> steps) => new()
    {
        RuleId        = ruleId,
        JobTypeId     = 1,
        ErrorTypeId   = 1,
        ActionToApply = "Composite test rule",
        ActionType    = FixActionType.Composite,
        ActionPayload = null,
        Enabled       = true,
        Steps         = steps,
    };

    private static FixPolicyRuleStep MakeStep(int order, FixActionType type, string payload) => new()
    {
        StepId        = order,
        StepOrder     = order,
        ActionType    = type,
        ActionPayload = payload,
    };

    private static AiRecommendation MakeRec(int failureId, int errorTypeId, int jobTypeId) => new()
    {
        RecommendationId = 1,
        FailureId        = failureId,
        ErrorTypeId      = errorTypeId,
        SuggestedAction  = "composite test",
        FixCategory      = FixCategory.Retry,
        ConfidenceScore  = 0.9m,
        Failure          = new JobFailure
        {
            FailureId     = failureId,
            JobTypeId     = jobTypeId,
            Status        = JobStatus.Failed,
            SourceLogPath = "n/a",
        },
    };

    private static Mock<IFixActionExecutor> StubExecutor(FixActionType type, bool returns)
    {
        var mock = new Mock<IFixActionExecutor>();
        mock.SetupGet(e => e.ActionType).Returns(type);
        mock.Setup(e => e.ExecuteAsync(It.IsAny<string?>(),
                It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixActionResult(returns));
        return mock;
    }
}
