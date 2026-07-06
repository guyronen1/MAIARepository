using Maia.Core.Entities;
using Maia.Core.Results;

namespace Maia.Core.Interfaces;

/// <summary>Filters for the paged audit-log query.</summary>
public sealed record AuditLogFilter(
    string?   EntityType = null,
    string?   EntityId   = null,
    string?   Actor      = null,
    string?   EventType  = null,
    DateTime? FromDate   = null,
    DateTime? ToDate     = null,
    int       Page       = 1,
    int       PageSize   = 50);

public interface IAuditRepository
{
    Task WriteAsync(AuditLog audit, CancellationToken ct = default);

    Task<PagedResult<AuditLog>> QueryAsync(AuditLogFilter filter, CancellationToken ct = default);
}
