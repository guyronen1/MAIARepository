using Maia.Core.Entities;

namespace Maia.API.Contracts;

public sealed record MonitoredJobDto(
    int                              MonitoredJobId,
    string                           Name,
    string?                          DisplayName,
    string                           JobTypeName,
    int                              PollingIntervalSeconds,
    bool                             IsActive,
    string?                          Description,
    DateTime                         CreatedAt,
    IReadOnlyList<ScanCheckRuleDto>  ScanCheckRules,
    IReadOnlyList<RuleOverrideDto>   Rules,
    IReadOnlyList<ScanSourceDto>     Sources,
    MonitoredJobLeaseDto?            Lease)
{
    public static MonitoredJobDto From(MonitoredJob m) => new(
        m.MonitoredJobId,
        m.Name,
        m.DisplayName,
        m.JobType?.Name        ?? m.JobTypeId.ToString(),
        m.PollingIntervalSeconds,
        m.IsActive,
        m.Description,
        m.CreatedAt,
        m.ScanCheckRules
            .Where(r => r.IsActive)
            .Select(ScanCheckRuleDto.From)
            .ToList(),
        m.JobRules
            .Where(jr => jr.IsActive && jr.Rule is not null)
            .Select(jr => RuleOverrideDto.From(jr.Rule!))
            .ToList(),
        m.ScanSources
            .Where(s => s.IsActive)
            .OrderBy(s => s.ScanSourceId)
            .Select(ScanSourceDto.From)
            .ToList(),
        MonitoredJobLeaseDto.From(m.Lease));
}

public sealed record ScanSourceDto(
    int       ScanSourceId,
    int       MonitoredJobId,
    string    Name,
    int       ScanTypeId,
    string    ScanTypeName,
    string?   LogFolder,
    string?   SearchPatterns,
    string?   InputFolder,
    bool      IncludeSubfolders,
    string?   ConnectionName,
    string?   LogSourceUrl,
    bool      IsActive,
    IReadOnlyList<ScanCheckRuleDto> ScanCheckRules)
{
    public static ScanSourceDto From(ScanSource s) => new(
        s.ScanSourceId,
        s.MonitoredJobId,
        s.Name,
        s.ScanTypeId,
        s.ScanTypeDefinition?.Name ?? s.ScanTypeId.ToString(),
        s.LogFolder,
        s.SearchPatterns,
        s.InputFolder,
        s.IncludeSubfolders,
        s.ConnectionName,
        s.LogSourceUrl,
        s.IsActive,
        s.ScanCheckRules
            .Where(r => r.IsActive)
            .Select(ScanCheckRuleDto.From)
            .ToList());
}

public sealed record MonitoredJobLeaseDto(
    string?   LeasedBy,
    DateTime? LeasedAt,
    DateTime? LeasedUntil,
    DateTime? NextEligibleAt,
    DateTime? LastRunStartedAt,
    DateTime? LastRunCompletedAt,
    string?   LastRunOutcome,
    string?   LastRunError,
    int?      LastRunDurationMs)
{
    public static MonitoredJobLeaseDto? From(MonitoredJobLease? l)
    {
        if (l is null) return null;
        int? durationMs = (l.LastRunStartedAt.HasValue && l.LastRunCompletedAt.HasValue)
            ? (int)Math.Clamp((l.LastRunCompletedAt.Value - l.LastRunStartedAt.Value).TotalMilliseconds,
                              0, int.MaxValue)
            : null;
        return new MonitoredJobLeaseDto(
            l.LeasedBy,
            l.LeasedAt,
            l.LeasedUntil,
            l.NextEligibleAt,
            l.LastRunStartedAt,
            l.LastRunCompletedAt,
            l.LastRunOutcome?.ToString(),
            l.LastRunError,
            durationMs);
    }
}

public sealed record ScanCheckRuleDto(
    int       CheckRuleId,
    string    CheckType,
    string?   SourceTable,
    string    TargetField,
    decimal?  MinValue,
    decimal?  MaxValue,
    string?   ExpectedValue,
    string?   WatermarkColumn,
    string?   SourceIdColumn,
    string?   ReferenceIdColumn,
    string?   FilePathColumn,
    string?   InputPathPattern,
    // FileContent
    string?   ExtractorType,
    string?   ExtractorLocator,
    string?   IdentifierLocator,
    string?   ExtractorPredicateType,
    string?   ExtractorPredicateValue,
    string    Severity,
    string?   Description)
{
    public static ScanCheckRuleDto From(ScanCheckRule r) => new(
        r.CheckRuleId,
        r.CheckType.ToString(),
        r.SourceTable,
        r.TargetField,
        r.MinValue,
        r.MaxValue,
        r.ExpectedValue,
        r.WatermarkColumn,
        r.SourceIdColumn,
        r.ReferenceIdColumn,
        r.FilePathColumn,
        r.InputPathPattern,
        r.ExtractorType?.ToString(),
        r.ExtractorLocator,
        r.IdentifierLocator,
        r.ExtractorPredicateType?.ToString(),
        r.ExtractorPredicateValue,
        r.Severity.ToString(),
        r.Description);
}

public sealed record RuleOverrideDto(
    int     RuleId,
    string  Pattern,
    string  ErrorTypeCode,
    decimal Confidence,
    int     Priority)
{
    public static RuleOverrideDto From(ClassificationRule r) => new(
        r.RuleId,
        r.Pattern,
        r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
        r.Confidence,
        r.Priority);
}
