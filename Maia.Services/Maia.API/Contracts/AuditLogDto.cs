using Maia.Core.Entities;

namespace Maia.API.Contracts;

public sealed record AuditLogDto(
    int       AuditId,
    int?      FailureId,
    string?   EntityType,
    string?   EntityId,
    string    EventType,
    string    Actor,
    string?   Detail,
    DateTime  Timestamp)
{
    public static AuditLogDto From(AuditLog a) => new(
        a.AuditId, a.FailureId, a.EntityType, a.EntityId,
        a.EventType, a.Actor, a.Detail, a.Timestamp);
}
