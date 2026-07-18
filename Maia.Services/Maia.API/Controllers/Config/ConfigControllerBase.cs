using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

/// <summary>
/// Shared base for the per-entity config controllers (ErrorTypes, MonitoredJobs,
/// ScanSources, ScanRules, FixPolicy, ClassificationRules) that were split out of
/// the former ~1700-line ConfigController. Each concrete controller keeps the
/// <c>api/config</c> route prefix and the Operator-read / Admin-write posture; this
/// base carries only the cross-cutting audit machinery so every write logs the same
/// shape (EntityType + EntityId discriminator, EventType = "{EntityType}{Verb}").
///
/// Audit actor is the authenticated principal (<c>currentUser.UserName</c>), resolved
/// server-side. Audit-write failures log at Error and never fail the request — the
/// operator's config change already succeeded; degraded audit beats a rolled-back UX.
/// </summary>
public abstract class ConfigControllerBase(
    IAuditRepository     audit,
    ICurrentUserAccessor currentUser,
    ILogger              logger) : ControllerBase
{
    // Audit actor = the authenticated principal. Guaranteed present: every write action
    // is gated by RequireAdmin, so no anonymous request reaches a body.
    protected string Actor => currentUser.UserName!;

    /// <summary>
    /// Write an AuditLog row. Never throws — failures are logged so they surface in
    /// ops monitoring but don't fail the request that already succeeded. If audit-write
    /// reliability becomes a concern, an outbox pattern fits cleanly on top of this.
    /// </summary>
    protected async Task WriteAuditAsync(
        string entityType, string entityId, string eventType,
        string actor, string detail, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(new AuditLog
            {
                EntityType = entityType,
                EntityId   = entityId,
                EventType  = eventType,
                Actor      = actor,
                Detail     = detail,
                Timestamp  = DateTime.Now,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Audit write failed: EventType={EventType} EntityType={EntityType} EntityId={EntityId}",
                eventType, entityType, entityId);
        }
    }

    /// <summary>
    /// Build the Detail string for an Updated event: "Field: before → after" joined by
    /// ", ", with only the fields that actually changed included. Returns empty string
    /// when nothing changed — caller decides whether to skip the audit write or emit a
    /// "No changes" placeholder.
    /// </summary>
    protected static string BuildDiff(params (string field, object? before, object? after)[] changes)
    {
        var changed = changes
            .Where(c => !Equals(c.before, c.after))
            .Select(c => $"{c.field}: {FormatValue(c.before)} → {FormatValue(c.after)}");
        return string.Join(", ", changed);
    }

    /// <summary>Render a value for the audit Detail string. Strings get single quotes,
    /// bools lowercase (matches JSON convention), null → "null".</summary>
    protected static string FormatValue(object? v) => v switch
    {
        null     => "null",
        string s => $"'{s}'",
        bool b   => b ? "true" : "false",
        _        => v.ToString() ?? "null",
    };
}
