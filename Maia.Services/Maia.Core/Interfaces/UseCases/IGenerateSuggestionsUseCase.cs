using Maia.Core.Results;

namespace Maia.Core.Interfaces.UseCases;

public interface IGenerateSuggestionsUseCase
{
    Task ExecuteAsync(IEnumerable<ClassificationResult> results, CancellationToken ct = default);
}
