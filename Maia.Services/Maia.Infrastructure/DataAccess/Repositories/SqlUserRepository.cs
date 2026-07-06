using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlUserRepository(IDbContextFactory<MaiaDbContext> factory) : IUserRepository
{
    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Username == username, ct);
    }

    public async Task<User?> GetByIdAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Marking the entity Modified attaches it and flags its scalar columns.
        // The untracked Role navigation is left alone (EF won't touch it), so a
        // user loaded with Include(Role) can be saved back without disturbing roles.
        db.Entry(user).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }
}
