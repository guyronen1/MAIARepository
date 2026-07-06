using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

public interface IFixLogRepository
{
    Task SaveAsync(FixExecutionLog log, CancellationToken ct = default);
}
