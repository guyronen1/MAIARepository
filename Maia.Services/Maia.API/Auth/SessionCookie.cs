using Maia.Core.Configuration;

namespace Maia.API.Auth;

/// <summary>
/// Builds the session cookie's attributes from <see cref="AuthOptions"/> so the
/// login endpoint, the per-request slide refresh (handler), and logout all agree.
///
/// SameSite=Strict is the primary CSRF defense — a cross-site request never carries
/// the cookie. Dev (localhost:4200 → :5095) is same-site (SameSite keys on the
/// registrable domain, not the port), so Strict works without friction. A future
/// deployment that splits SPA and API across different registrable domains would
/// need SameSite=None + Secure + an antiforgery token (documented follow-up).
/// </summary>
public static class SessionCookie
{
    public static CookieOptions Build(AuthOptions o) => new()
    {
        HttpOnly    = true,
        Secure      = o.CookieSecure,
        SameSite    = SameSiteMode.Strict,
        Path        = "/",
        IsEssential = true,
        Expires     = DateTimeOffset.Now.AddSeconds(o.IdleTimeoutSeconds),
    };

    /// <summary>Deletion options must match the non-expiry attributes the cookie was
    /// written with, or the browser keeps the original.</summary>
    public static CookieOptions BuildForDelete(AuthOptions o) => new()
    {
        HttpOnly = true,
        Secure   = o.CookieSecure,
        SameSite = SameSiteMode.Strict,
        Path     = "/",
    };
}
