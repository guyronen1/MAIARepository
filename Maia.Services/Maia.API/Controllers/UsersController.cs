using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Security;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// User/role administration. Admin-only. Mirrors ConfigController's audit posture:
/// every write logs an AuditLog row (EntityType="User") with the acting admin as
/// Actor, and audit-write failures are logged-and-swallowed (never fail the request).
///
/// No email/SMTP flows in v1 — password reset is admin-initiated and sets a temp
/// password + MustChangePassword=true so the user rotates on next login. Created
/// users are likewise born with MustChangePassword=true.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = "RequireAdmin")]
public class UsersController(
    IDbContextFactory<MaiaDbContext> dbFactory,
    IPasswordHasher                hasher,
    IAuditRepository               audit,
    ICurrentUserAccessor           currentUser,
    ILogger<UsersController>       logger) : ControllerBase
{
    public sealed record CreateUserRequest(string Username, string Password, string Role);
    public sealed record UpdateUserRequest(string Role, bool IsActive);
    public sealed record ResetPasswordRequest(string NewPassword);

    private string Actor => currentUser.UserName!;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.Users
            .Include(u => u.Role)
            .OrderBy(u => u.Username)
            .Select(u => new
            {
                u.UserId,
                u.Username,
                Role = u.Role!.Name,
                u.IsActive,
                u.MustChangePassword,
                u.CreatedAt,
                u.LastLoginAt,
            })
            .ToListAsync(ct);
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(new { error = "UsernameRequired", message = "Username is required." });
        if (PasswordPolicy.Validate(req.Password) is { } pwErr)
            return BadRequest(new { error = "PasswordTooShort", message = pwErr });
        if (!TryResolveRole(req.Role, out var roleId))
            return BadRequest(new { error = "UnknownRole", message = "Role must be User, Operator, or Administrator." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Username == req.Username, ct))
            return Conflict(new { error = "DuplicateUsername", message = $"A user named '{req.Username}' already exists." });

        var user = new User
        {
            Username           = req.Username.Trim(),
            PasswordHash       = hasher.Hash(req.Password),
            RoleId             = roleId,
            IsActive           = true,
            MustChangePassword = true,   // force rotation off the admin-set temp password
            CreatedAt          = DateTime.Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        await WriteAudit(user.UserId, "UserCreated",
            $"Created user '{user.Username}' (Role={req.Role}, MustChangePassword=true)", ct);
        return Ok(new { user.UserId });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest req, CancellationToken ct)
    {
        if (!TryResolveRole(req.Role, out var roleId))
            return BadRequest(new { error = "UnknownRole", message = "Role must be User, Operator, or Administrator." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == id, ct);
        if (user is null) return NotFound();

        // Lockout guard: don't let the last active administrator be demoted or disabled,
        // or the system becomes unmanageable (no one can administer users/config).
        var losingAdmin = user.RoleId == (int)MaiaRole.Administrator
                          && (roleId != (int)MaiaRole.Administrator || !req.IsActive);
        if (losingAdmin)
        {
            var otherActiveAdmins = await db.Users.CountAsync(
                u => u.UserId != id && u.IsActive && u.RoleId == (int)MaiaRole.Administrator, ct);
            if (otherActiveAdmins == 0)
                return Conflict(new { error = "LastAdmin", message = "Cannot demote or disable the last active administrator." });
        }

        var beforeRole   = user.Role!.Name;
        var beforeActive = user.IsActive;
        user.RoleId   = roleId;
        user.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);

        await WriteAudit(id, "UserUpdated",
            $"Role: {beforeRole} → {req.Role}, IsActive: {beforeActive} → {req.IsActive}", ct);
        return NoContent();
    }

    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest req, CancellationToken ct)
    {
        if (PasswordPolicy.Validate(req.NewPassword) is { } pwErr)
            return BadRequest(new { error = "PasswordTooShort", message = pwErr });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return NotFound();

        user.PasswordHash       = hasher.Hash(req.NewPassword);
        user.MustChangePassword = true;
        await db.SaveChangesAsync(ct);

        await WriteAudit(id, "UserPasswordReset",
            $"Reset password for user '{user.Username}' (MustChangePassword=true)", ct);
        return NoContent();
    }

    /// <summary>Parse a role name → RoleId (RoleId == (int)MaiaRole). Rejects numeric
    /// strings and undefined values.</summary>
    private static bool TryResolveRole(string? role, out int roleId)
    {
        if (!string.IsNullOrWhiteSpace(role)
            && Enum.TryParse<MaiaRole>(role, ignoreCase: true, out var r)
            && Enum.IsDefined(r))
        {
            roleId = (int)r;
            return true;
        }
        roleId = 0;
        return false;
    }

    private async Task WriteAudit(int userId, string eventType, string detail, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(new AuditLog
            {
                EntityType = "User",
                EntityId   = userId.ToString(),
                EventType  = eventType,
                Actor      = Actor,
                Detail     = detail,
                Timestamp  = DateTime.Now,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write failed: EventType={EventType} User={UserId}", eventType, userId);
        }
    }
}
