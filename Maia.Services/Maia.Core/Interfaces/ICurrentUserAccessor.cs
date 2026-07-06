using Maia.Core.Enums;

namespace Maia.Core.Interfaces;

/// <summary>
/// Server-authoritative view of the current request's authenticated principal.
/// Backed by the auth handler's claims (Infrastructure/API binds it to HttpContext).
/// Used to stamp the real actor on audit rows / operator actions — replacing the
/// client-supplied operatorId, which must never be trusted once auth is live.
///
/// All members are null/false when the request is anonymous.
/// </summary>
public interface ICurrentUserAccessor
{
    bool IsAuthenticated { get; }
    int? UserId { get; }
    string? UserName { get; }
    MaiaRole? Role { get; }

    /// <summary>True when the authenticated user still owes a password rotation
    /// (seeded admin / admin-reset). Enforced as an access gate in a later phase.</summary>
    bool MustChangePassword { get; }
}
