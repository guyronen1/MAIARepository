using System.Security.Cryptography;
using Maia.Core.Configuration;
using Maia.Core.Entities;
using Maia.Core.Interfaces;

namespace Maia.Application.Security;

/// <summary>
/// <see cref="IAuthService"/> over the user/session repositories + password hasher.
/// Sessions are server-side (opaque token), so logout and disable take effect
/// immediately and roles are looked up live on each request via the session's User.
/// Local time throughout, matching the codebase convention.
/// </summary>
public sealed class AuthService(
    IUserRepository    users,
    ISessionRepository sessions,
    IPasswordHasher    hasher,
    AuthOptions        options) : IAuthService
{
    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await users.GetByUsernameAsync(username, ct);
        if (user is null || !user.IsActive)
            return new LoginResult(false, null, null, false);

        var verify = hasher.Verify(user.PasswordHash, password);
        if (verify == PasswordVerificationResult.Failed)
            return new LoginResult(false, null, null, false);

        // Transparent upgrade: a valid hash made with older parameters gets re-hashed
        // on this successful login (no separate migration). Persisted with LastLoginAt.
        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.Hash(password);

        var now   = DateTime.Now;
        var token = GenerateToken();
        await sessions.CreateAsync(new UserSession
        {
            Token          = token,
            UserId         = user.UserId,
            CreatedAt      = now,
            LastActivityAt = now,
        }, ct);

        user.LastLoginAt = now;
        await users.UpdateAsync(user, ct);

        return new LoginResult(true, user, token, user.MustChangePassword);
    }

    public Task LogoutAsync(string token, CancellationToken ct = default)
        => sessions.DeleteByTokenAsync(token, ct);

    public async Task<bool> ChangePasswordAsync(
        int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
            return false;
        if (hasher.Verify(user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            return false;

        user.PasswordHash       = hasher.Hash(newPassword);
        user.MustChangePassword = false;
        await users.UpdateAsync(user, ct);
        return true;
    }

    public async Task DismissPasswordChangeAsync(int userId, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(userId, ct);
        if (user is null || !user.MustChangePassword) return;
        user.MustChangePassword = false;
        await users.UpdateAsync(user, ct);
    }

    public async Task<UserSession?> ValidateSessionAsync(string token, CancellationToken ct = default)
    {
        var session = await sessions.GetByTokenAsync(token, ct);
        // Live checks: session exists, user still active. A disabled/deleted user
        // loses access on their next request, not next login.
        if (session?.User is null || !session.User.IsActive)
            return null;

        var now  = DateTime.Now;
        var idle = (now - session.LastActivityAt).TotalSeconds;

        if (idle > options.IdleTimeoutSeconds)
        {
            // Past the idle window — expire lazily and clean the row up.
            await sessions.DeleteByTokenAsync(token, ct);
            return null;
        }

        // Slide forward, throttled so we don't write on every request.
        if (idle >= options.ActivitySlideThrottleSeconds)
        {
            await sessions.UpdateActivityAsync(session.SessionId, now, ct);
            session.LastActivityAt = now;
        }

        return session;
    }

    /// <summary>256-bit cryptographically-random opaque token, URL-safe base64.</summary>
    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
