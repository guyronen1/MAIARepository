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
}
