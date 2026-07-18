using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

public interface IUserRepository
{
    /// <summary>Load a user by username (case-insensitive via the DB collation),
    /// including its <see cref="User.Role"/>. Null when not found.</summary>
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Load a user by id, including its <see cref="User.Role"/>.</summary>
    Task<User?> GetByIdAsync(int userId, CancellationToken ct = default);

    /// <summary>Persist scalar changes to an existing user (e.g. LastLoginAt,
    /// PasswordHash, MustChangePassword, IsActive). The Role navigation is ignored.</summary>
    Task UpdateAsync(User user, CancellationToken ct = default);

    /// <summary>All users with their <see cref="User.Role"/>, ordered by Username.
    /// For the admin list screen — the caller projects to safe fields.</summary>
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default);

    /// <summary>True when a user with this exact username already exists
    /// (case per the DB collation). Used to reject duplicate creates.</summary>
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);

    /// <summary>Insert a new user; returns the generated <see cref="User.UserId"/>.</summary>
    Task<int> AddAsync(User user, CancellationToken ct = default);

    /// <summary>Count ACTIVE users whose role is <paramref name="adminRoleId"/>,
    /// excluding <paramref name="excludingUserId"/> — the last-admin lockout guard.
    /// The admin role id is passed in so the repository stays policy-free.</summary>
    Task<int> CountActiveAdminsExceptAsync(int excludingUserId, int adminRoleId, CancellationToken ct = default);
}
