using System.Security.Claims;
using System.Text.Encodings.Web;
using Maia.Core.Configuration;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Maia.API.Auth;

/// <summary>
/// Validates the opaque session token carried in the httpOnly cookie and builds the
/// request principal. Role is looked up LIVE (via the session's User) on every request,
/// so demotion/disable take effect on the next request — the API, not the login, is
/// the boundary.
///
/// A missing or invalid/expired token yields <see cref="AuthenticateResult.NoResult"/>,
/// NOT a failure — this handler does AUTHENTICATION only, never rejection. Enforcement
/// is LIVE at the AUTHORIZATION layer: the default fallback policy
/// (RequireAuthenticatedUser) rejects anonymous requests, and per-endpoint role policies
/// (RequireOperator / RequireAdmin) gate the rest; anonymous endpoints opt out via
/// [AllowAnonymous]. Keeping the handler non-rejecting lets authorization own the 401
/// (unauthenticated) vs 403 (wrong role) distinction cleanly.
///
/// On a successful authentication the cookie is re-issued with a fresh sliding expiry
/// (header-only; the DB activity slide is throttled inside <see cref="IAuthService"/>).
/// </summary>
public sealed class MaiaSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAuthService auth,
    AuthOptions authOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "MaiaSession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(authOptions.CookieName, out var token) || string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var session = await auth.ValidateSessionAsync(token, Context.RequestAborted);
        if (session?.User is null)
            // Invalid/expired/disabled → treat as anonymous; the authorization fallback
            // policy rejects it downstream (this handler never rejects).
            return AuthenticateResult.NoResult();

        var user = session.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name,           user.Username),
            new(ClaimTypes.Role,           user.Role?.Name ?? MaiaRoleName(user.RoleId)),
        };
        if (user.MustChangePassword)
            claims.Add(new Claim(MaiaClaims.MustChangePassword, "true"));

        // Sliding cookie refresh — fresh Expires each authenticated request so an
        // active session survives a browser restart within the idle window.
        Response.Cookies.Append(authOptions.CookieName, token, SessionCookie.Build(authOptions));

        var identity  = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    // Fallback if the Role nav somehow wasn't loaded — keep the role claim populated
    // from the FK (RoleId == (int)MaiaRole) rather than emitting an empty role.
    private static string MaiaRoleName(int roleId) =>
        Enum.IsDefined(typeof(MaiaRole), roleId)
            ? ((MaiaRole)roleId).ToString()
            : roleId.ToString();
}
