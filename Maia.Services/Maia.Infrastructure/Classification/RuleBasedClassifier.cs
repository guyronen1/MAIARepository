using Maia.Core.Classification;
using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Maia.Core.Results;

namespace Maia.Infrastructure.Classification;

/// <summary>
/// Pattern-matching classifier driven by ClassificationRules from the database.
/// When a MonitoredJob is set on the failure, per-job rule overrides are used first.
///
/// <para>Match semantics: case-insensitive substring containment. The single
/// supported wildcard is <c>*</c>, which matches any run of characters (including
/// none). All other regex metacharacters are treated as literal text.</para>
///
/// <para>To plug in ML: implement <see cref="IClassificationStrategy"/> and swap
/// this registration.</para>
/// </summary>
public sealed class RuleBasedClassifier(
    IClassificationRuleRepository ruleRepo,
    IMonitoredJobRepository monitoredJobRepo,
    ILogParser parser) : IClassificationStrategy
{
    public async Task<ClassificationResult?> ClassifyAsync(
        JobFailure job,
        string logContent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(logContent))
            return null;

        var rules = job.MonitoredJobId.HasValue
            ? await monitoredJobRepo.GetEffectiveRulesAsync(job.MonitoredJobId.Value, ct)
            : await ruleRepo.GetByJobTypeAsync(job.JobTypeId, ct);

        var lines = parser.ParseLog(logContent);

        foreach (var rule in rules)
        {
            var match = lines.FirstOrDefault(l => ClassificationMatcher.IsMatch(l, rule.Pattern));

            if (match is not null)
            {
                return new ClassificationResult
                {
                    FailureId       = job.FailureId,
                    JobId           = job.JobId,
                    JobTypeId       = job.JobTypeId,
                    // Forward to downstream so the suggestion generator can
                    // pick a per-job override over the default policy.
                    MonitoredJobId  = job.MonitoredJobId,
                    ErrorTypeId     = rule.ErrorTypeId,
                    ErrorTypeCode   = rule.ErrorType?.Code ?? string.Empty,
                    RawError        = match.Trim(),
                    Confidence      = (double)rule.Confidence,
                };
            }
        }

        return null;
    }
}
