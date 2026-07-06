namespace Maia.Core.Interfaces;

/// <summary>
/// Save-time guard for SqlScript fix payloads (single-action policies and
/// composite SqlScript steps) — the WRITE path. A fix runs UPDATE/DELETE/EXEC
/// against the source DB under a write-capable login, so an unscoped statement
/// (e.g. <c>UPDATE dbo.Files SET FileStatusCode = 1</c> with no WHERE) would
/// mutate the whole table instead of the one failing row.
///
/// This is layer 1 of the validation framework (hard server-side block) — the
/// read-side <c>SqlQuery</c> scan path uses only a soft warning; the write side
/// must block. It catches accidental / bulk fixes; it does NOT catch deliberate
/// bypasses (tautologies) — that's covered by the trust model + audit logs.
/// </summary>
public interface ISqlFixScopeValidator
{
    /// <summary>
    /// Returns <c>null</c> if the SqlScript fix payload is acceptably scoped —
    /// every statement is an UPDATE/DELETE whose WHERE references <c>{sourceId}</c>,
    /// or an EXEC that passes <c>{sourceId}</c> as a parameter. Otherwise returns
    /// a human-readable reason it was rejected (for a 400 message).
    /// <c>{failureId}</c> deliberately does NOT count — it's MAIA's internal PK,
    /// not a key into the operator's table.
    /// </summary>
    string? Validate(string payload);
}
