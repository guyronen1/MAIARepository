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

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users
            .Include(u => u.Role)
            .OrderBy(u => u.Username)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(u => u.Username == username, ct);
    }

    public async Task<int> AddAsync(User user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user.UserId;
    }

    public async Task<int> CountActiveAdminsExceptAsync(
        int excludingUserId, int adminRoleId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.CountAsync(
            u => u.UserId != excludingUserId && u.IsActive && u.RoleId == adminRoleId, ct);
    }

    public async Task<IReadOnlySet<int>> GetAssignedJobIdsAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.UserMonitoredJobs
            .Where(a => a.UserId == userId)
            .Select(a => a.MonitoredJobId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<int, int>> GetAssignedJobCountsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserMonitoredJobs
            .GroupBy(a => a.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);
    }

    public async Task SetJobAccessAsync(
        int userId, bool restrictToAssignedJobs, IReadOnlyCollection<int> jobIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users
            .Include(u => u.JobAccess)
            .FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw new InvalidOperationException($"User {userId} not found.");

        user.RestrictToAssignedJobs = restrictToAssignedJobs;

        // Diff against the current set so unchanged rows aren't deleted and re-added.
        var wanted = jobIds.ToHashSet();
        foreach (var row in user.JobAccess.Where(a => !wanted.Contains(a.MonitoredJobId)).ToList())
            user.JobAccess.Remove(row);
        foreach (var id in wanted.Except(user.JobAccess.Select(a => a.MonitoredJobId)).ToList())
            user.JobAccess.Add(new UserMonitoredJob { UserId = userId, MonitoredJobId = id });

        await db.SaveChangesAsync(ct);   // single SaveChanges = one implicit transaction
    }
}
