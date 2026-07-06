using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

/// <summary>
/// Authentication orchestration: credential verification, server-side session
/// lifecycle (create / validate-with-sliding-idle / revoke), and self-service
/// password change. Implemented in the Application layer over the user/session
/// repositories and <see cref="IPasswordHasher"/>.
/// </summary>
public interface IAuthService
{
    /// <summary>Verify credentials and, on success, open a session. Returns a
    /// result with <see cref="LoginResult.Success"/> = false for unknown user,
    /// disabled account, or bad password — callers must not distinguish these to
    /// the client (avoid user enumeration).</summary>
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Revoke a session by its token. No-op if the token is unknown.</summary>
    Task LogoutAsync(string token, CancellationToken ct = default);

    /// <summary>Change the caller's own password after verifying the current one.
    /// Clears <see cref="User.MustChangePassword"/>. Returns false when the user is
    /// gone/disabled or the current password is wrong.</summary>
    Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>Acknowledge the one-time change-password prompt without changing the
    /// password ("Skip"). Clears <see cref="User.MustChangePassword"/> so it won't
    /// prompt again. No-op if the user is gone or the flag is already clear.</summary>
    Task DismissPasswordChangeAsync(int userId, CancellationToken ct = default);

    /// <summary>Validate a session token: returns the live session (with User+Role)
    /// when present, active, and within the idle window; null otherwise. Slides
    /// <see cref="UserSession.LastActivityAt"/> forward (throttled) and deletes a
    /// session that has passed the idle window.</summary>
    Task<UserSession?> ValidateSessionAsync(string token, CancellationToken ct = default);
}

/// <summary>Outcome of a login attempt. On success carries the loaded user (with
/// Role) and the freshly-minted session token the caller sets as the cookie.</summary>
public sealed record LoginResult(bool Success, User? User, string? Token, bool MustChangePassword);
