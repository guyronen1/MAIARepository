using Maia.API.Auth;
using Maia.Core.Configuration;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

/// <summary>
/// Authentication endpoints. Login/logout manage the httpOnly session cookie;
/// change-password is self-service; /me lets the SPA discover the current identity.
/// <c>[AllowAnonymous]</c> on login/me keeps them reachable under the default-closed
/// fallback policy; logout + change-password + dismiss require a session.
/// A seeded/reset user must rotate before using the app (MustChangePasswordMiddleware);
/// the only skip is the Development-only dismiss endpoint (fails closed elsewhere).
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(
    IAuthService          auth,
    ICurrentUserAccessor  currentUser,
    AuthOptions           options,
    IWebHostEnvironment   env,
    IConfiguration        config,
    IJobAccessScope       jobAccess,
    IUserRepository       users) : ControllerBase
{
    public sealed record LoginRequest(string Username, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    // Skipping a forced password change is a DEVELOPMENT-ONLY convenience. Fails closed:
    // any non-Development environment (incl. unset → Production) cannot skip, so the
    // forced rotation is the shipped default. This is the single env-gated point.
    private bool SkipAllowed => env.IsDevelopment();

    // Knowledge-assistant UI toggle (appsettings KnowledgeAssistant:Enabled). Missing key = false,
    // so the AI button stays hidden unless a deployment opts in. UX gating only, like canSkipPasswordChange.
    private bool AssistantEnabled => config.GetValue<bool>("KnowledgeAssistant:Enabled");

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "MissingCredentials", message = "Username and password are required." });

        var result = await auth.LoginAsync(req.Username, req.Password, ct);
        if (!result.Success)
            // Single generic message for unknown user / disabled / bad password —
            // don't leak which it was.
            return Unauthorized(new { error = "InvalidCredentials", message = "Invalid username or password." });

        Response.Cookies.Append(options.CookieName, result.Token!, SessionCookie.Build(options));

        // This request is still anonymous (the cookie was only just issued), so the job
        // scope comes from the loaded user row rather than IJobAccessScope.
        var user = result.User!;
        var restricted = user.RoleId != (int)MaiaRole.Administrator && user.RestrictToAssignedJobs;
        var assignedCount = restricted ? (await users.GetAssignedJobIdsAsync(user.UserId, ct)).Count : 0;

        return Ok(new
        {
            username             = user.Username,
            role                 = user.Role?.Name,
            mustChangePassword   = result.MustChangePassword,
            canSkipPasswordChange = SkipAllowed,
            knowledgeAssistantEnabled = AssistantEnabled,
            jobScope             = restricted ? "assigned" : "all",
            assignedJobCount     = assignedCount,
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(options.CookieName, out var token) && !string.IsNullOrEmpty(token))
            await auth.LogoutAsync(token, ct);

        Response.Cookies.Delete(options.CookieName, SessionCookie.BuildForDelete(options));
        return NoContent();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        // Self-service: identity comes from the authenticated principal, never the body.
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return Unauthorized();
        if (req is null || string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { error = "MissingNewPassword", message = "A new password is required." });
        if (Maia.Core.Security.PasswordPolicy.Validate(req.NewPassword) is { } pwErr)
            return BadRequest(new { error = "PasswordTooShort", message = pwErr });

        var ok = await auth.ChangePasswordAsync(userId, req.CurrentPassword ?? string.Empty, req.NewPassword, ct);
        if (!ok)
            return BadRequest(new { error = "CurrentPasswordIncorrect", message = "Current password is incorrect." });

        return NoContent();
    }

    /// <summary>DEVELOPMENT-ONLY "Skip for now": clears the MustChangePassword flag
    /// without changing the password. Fails closed — in any non-Development environment
    /// this returns 403, so the forced rotation cannot be skipped in a real deployment.
    /// </summary>
    [HttpPost("dismiss-password-change")]
    public async Task<IActionResult> DismissPasswordChange(CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return Unauthorized();
        if (!SkipAllowed)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error   = "SkipNotAllowed",
                message = "Password change is mandatory in this environment.",
            });
        await auth.DismissPasswordChangeAsync(userId, ct);
        return NoContent();
    }

    /// <summary>Current identity for the SPA. Anonymous returns 200 with
    /// <c>authenticated:false</c> (not 401) so the client can branch without tripping
    /// its 401-redirect interceptor. <c>jobScope</c> ("all" | "assigned") and
    /// <c>assignedJobCount</c> drive the "no jobs assigned to you" empty state — UX only;
    /// the API filters every read by the same scope.</summary>
    [AllowAnonymous]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated)
            return Ok(new { authenticated = false });

        var assigned = await jobAccess.GetVisibleJobIdsAsync(ct);

        return Ok(new
        {
            authenticated         = true,
            username              = currentUser.UserName,
            role                  = currentUser.Role?.ToString(),
            mustChangePassword    = currentUser.MustChangePassword,
            canSkipPasswordChange = SkipAllowed,
            knowledgeAssistantEnabled = AssistantEnabled,
            jobScope              = assigned is null ? "all" : "assigned",
            assignedJobCount      = assigned?.Count ?? 0,
        });
    }
}
