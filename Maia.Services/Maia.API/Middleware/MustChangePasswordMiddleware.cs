using Maia.API.Auth;

namespace Maia.API.Middleware;

/// <summary>
/// Forces a password rotation: an authenticated user who still owes one is blocked
/// from every <c>/api/*</c> call except the escape hatches until they change it.
/// Runs after authentication (needs the principal) and before authorization.
///
/// This is the entire mitigation for the bootstrap admin credential living in source
/// control (known-compromised by design): the seed is unusable for anything but its
/// own rotation. Enforcement is UNCONDITIONAL here — there is no environment switch in
/// this middleware. The only way past the block is to actually change the password,
/// OR the Development-only skip (POST /api/auth/dismiss-password-change), which itself
/// is fail-closed to Development in the controller. Scoped to <c>/api/*</c> so it never
/// touches SPA assets or the K8s health probes; auth self-service routes are allow-listed.
///
/// Returns 403 with reason <c>PasswordChangeRequired</c> so the frontend's error
/// interceptor routes to /change-password — distinct from a plain 403 (wrong role) and
/// a 401 (no session).
/// </summary>
public sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowList =
    {
        "/api/auth/change-password",
        "/api/auth/dismiss-password-change",   // dev-only skip; itself fail-closed in the controller
        "/api/auth/logout",
        "/api/auth/me",
    };

    public async Task Invoke(HttpContext ctx)
    {
        var user = ctx.User;
        var path = ctx.Request.Path;

        if (user.Identity?.IsAuthenticated == true
            && user.HasClaim(MaiaClaims.MustChangePassword, "true")
            && path.StartsWithSegments("/api")
            && !AllowList.Any(a => path.StartsWithSegments(a)))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error   = "PasswordChangeRequired",
                message = "You must change your password before continuing.",
            });
            return;
        }

        await next(ctx);
    }
}
