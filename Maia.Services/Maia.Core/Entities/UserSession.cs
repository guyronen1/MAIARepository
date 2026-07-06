namespace Maia.Core.Entities;

/// <summary>
/// Server-side session. Enables sliding-idle expiry, revocation (logout/disable),
/// and live role lookup — the auth handler reloads the user's role on every request
/// from this row's <see cref="User"/>, so a demoted/disabled user loses access on
/// their next request, not next login.
///
/// Named UserSession (not "Session") to avoid colliding with ASP.NET HTTP session.
/// No role is stored here — it's looked up live via the User FK. <see cref="LastActivityAt"/>
/// is the authoritative expiry input (now - LastActivityAt &gt; idle window =&gt; expired).
/// </summary>
public class UserSession
{
    public int SessionId { get; set; }

    /// <summary>Opaque, cryptographically random token carried in the httpOnly
    /// session cookie. Unique-indexed for the per-request lookup.</summary>
    public required string Token { get; set; }

    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivityAt { get; set; }

    public User? User { get; set; }
}
