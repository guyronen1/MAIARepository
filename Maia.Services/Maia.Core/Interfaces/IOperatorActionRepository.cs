using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

public interface IOperatorActionRepository
{
    Task SaveAsync(OperatorAction action, CancellationToken ct = default);
}
