using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

public interface IMonitoredJobRepository
{
    Task<List<MonitoredJob>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>Returns all active jobs including JobType, JobRules → Rule → ErrorType (for the data view).</summary>
    Task<List<MonitoredJob>> GetActiveWithRulesAsync(CancellationToken ct = default);

    Task<MonitoredJob?> GetByIdAsync(int monitoredJobId, CancellationToken ct = default);
    Task<MonitoredJob?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the effective classification rules for a job:
    /// if per-job overrides exist (MonitoredJobRules), returns those ordered by priority;
    /// otherwise falls back to the type-level rules for the job's JobTypeId.
    /// </summary>
    Task<List<ClassificationRule>> GetEffectiveRulesAsync(int monitoredJobId, CancellationToken ct = default);

    Task<List<MonitoredJob>> GetAllWithRulesAsync(CancellationToken ct = default);
    Task<MonitoredJob> SaveAsync(MonitoredJob job, CancellationToken ct = default);
    Task UpdateAsync(MonitoredJob job, CancellationToken ct = default);
    Task DeleteAsync(int monitoredJobId, CancellationToken ct = default);
}
