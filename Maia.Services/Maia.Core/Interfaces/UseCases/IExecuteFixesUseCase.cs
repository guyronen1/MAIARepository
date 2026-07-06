namespace Maia.Core.Interfaces.UseCases;

public interface IExecuteFixesUseCase
{
    Task ExecuteAsync(CancellationToken ct = default);
}
