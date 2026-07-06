using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

public interface IClassificationRuleRepository
{
    Task<List<ClassificationRule>> GetByJobTypeAsync(int jobTypeId, CancellationToken ct = default);
    Task<List<ClassificationRule>> GetAllAsync(CancellationToken ct = default);
    Task<ClassificationRule> SaveAsync(ClassificationRule rule, CancellationToken ct = default);
    Task UpdateAsync(ClassificationRule rule, CancellationToken ct = default);
    Task DeleteAsync(int ruleId, CancellationToken ct = default);
}
