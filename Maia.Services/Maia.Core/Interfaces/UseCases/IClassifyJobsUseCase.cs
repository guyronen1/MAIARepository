using Maia.Core.Entities;
using Maia.Core.Results;

namespace Maia.Core.Interfaces.UseCases;

public interface IClassifyJobsUseCase
{
    Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(IEnumerable<JobFailure> jobList, CancellationToken ct = default);
}
