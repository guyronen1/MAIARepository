using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

public interface ISessionRepository
{
    Task<UserSession> CreateAsync(UserSession session, CancellationToken ct = default);

    /// <summary>Load a session by its opaque token, including <see cref="UserSession.User"/>
    /// and the user's <see cref="User.Role"/> for the live per-request role lookup.
    /// Null when not found.</summary>
    Task<UserSession?> GetByTokenAsync(string token, CancellationToken ct = default);

    /// <summary>Slide the idle window forward (single-column UPDATE).</summary>
    Task UpdateActivityAsync(int sessionId, DateTime lastActivityAt, CancellationToken ct = default);

    /// <summary>Revoke a session (logout). No-op when the token isn't found.</summary>
    Task DeleteByTokenAsync(string token, CancellationToken ct = default);
}
