using Maia.Core.Results;

namespace Maia.Core.Interfaces.UseCases;

public interface IDirectoryPipelineUseCase
{
    Task<DirectoryPipelineResult> ExecuteAsync(
        string directoryPath,
        string searchPattern = "*.log",
        bool recursive = true,
        CancellationToken ct = default);
}
