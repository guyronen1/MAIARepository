using Maia.Core.Entities;
using Maia.Core.Enums;

namespace Maia.Core.Interfaces;

/// <summary>
/// Strategy for executing a specific category of fix.
/// Register one implementation per FixCategory; DefaultFixEngine dispatches to the matching handler.
/// </summary>
public interface IFixHandler
{
    FixCategory Category { get; }
    Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default);
}
