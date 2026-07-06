using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlMonitoredJobLeaseRepository(IDbContextFactory<MaiaDbContext> factory)
    : IMonitoredJobLeaseRepository
{
    // Single atomic UPDATE TOP (N) ... OUTPUT inserted.* with READPAST so concurrent
    // workers walk past each other's locked rows rather than blocking. UPDLOCK + ROWLOCK
    // hold the row until commit so post-update state is visible to the other tx (or skipped).
    private const string ClaimSql = """
        DECLARE @now datetime2(3) = SYSDATETIME();

        UPDATE TOP (@batchSize) L
        SET
            LeasedBy         = @leasedBy,
            LeasedAt         = @now,
            LeasedUntil      = DATEADD(SECOND, S.LeaseDurationSeconds, @now),
            LastRunStartedAt = @now,
            LastRunOutcome   = NULL,
            LastRunError     = NULL
        OUTPUT
            inserted.MonitoredJobId  AS MonitoredJobId,
            S.LeaseDurationSeconds   AS LeaseDurationSeconds,
            inserted.LeasedUntil     AS LeasedUntil
        FROM dbo.MonitoredJobLeases L WITH (READPAST, UPDLOCK, ROWLOCK)
        JOIN dbo.MonitoredJobs      J ON J.MonitoredJobId = L.MonitoredJobId
        -- Tier 2.5: lease duration = MAX over the job's ACTIVE sources' ScanType
        -- durations. The job-level ScanTypeId is no longer authoritative (a job is a
        -- container of typed sources). Single-source jobs => MAX of one => byte-identical
        -- to the old J.ScanTypeId lookup. MAX over zero rows yields a single NULL row, so
        -- the IS NOT NULL guard below excludes jobs with no active sources from being
        -- claimed at all (nothing to scan — no futile claim/release churn).
        CROSS APPLY (
            SELECT MAX(st.LeaseDurationSeconds) AS LeaseDurationSeconds
            FROM dbo.ScanSources src
            JOIN dbo.ScanTypes  st ON st.ScanTypeId = src.ScanTypeId
            WHERE src.MonitoredJobId = L.MonitoredJobId AND src.IsActive = 1
        ) S
        WHERE J.IsActive = 1
          AND S.LeaseDurationSeconds IS NOT NULL
          AND L.NextEligibleAt <= @now
          AND (L.LeasedUntil IS NULL OR L.LeasedUntil < @now);
        """;

    public async Task<IReadOnlyList<ClaimedJobLease>> ClaimAsync(
        string leasedBy, int batchSize, CancellationToken ct)
    {
        if (batchSize <= 0) return Array.Empty<ClaimedJobLease>();

        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();  // EF-owned: do NOT dispose
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ClaimSql;
        cmd.Parameters.Add(new SqlParameter("@leasedBy",  leasedBy));
        cmd.Parameters.Add(new SqlParameter("@batchSize", batchSize));

        var claimed = new List<ClaimedJobLease>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            claimed.Add(new ClaimedJobLease(
                MonitoredJobId:       reader.GetInt32(0),
                LeaseDurationSeconds: reader.GetInt32(1),
                LeasedUntil:          reader.GetDateTime(2)));
        }
        return claimed;
    }

    public async Task<bool> ReleaseAsync(
        int monitoredJobId, string leasedBy, JobRunOutcome outcome,
        int nextPollingIntervalSeconds, string? error, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // ExecuteUpdate with the LeasedBy guard — if another worker stole the lease,
        // RowsAffected will be 0 and we leave their state alone.
        var truncatedError = error is null
            ? null
            : error.Length > 2000 ? error[..2000] : error;

        var rows = await db.MonitoredJobLeases
            .Where(l => l.MonitoredJobId == monitoredJobId && l.LeasedBy == leasedBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.LeasedBy,           (string?)null)
                .SetProperty(l => l.LeasedUntil,        (DateTime?)null)
                .SetProperty(l => l.LastRunCompletedAt, DateTime.Now)
                .SetProperty(l => l.LastRunOutcome,     (JobRunOutcome?)outcome)
                .SetProperty(l => l.LastRunError,       truncatedError)
                .SetProperty(l => l.NextEligibleAt,     DateTime.Now.AddSeconds(nextPollingIntervalSeconds)),
            ct);

        return rows > 0;
    }

    public async Task<bool> HeartbeatAsync(
        int monitoredJobId, string leasedBy, int extendSeconds, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var rows = await db.MonitoredJobLeases
            .Where(l => l.MonitoredJobId == monitoredJobId
                     && l.LeasedBy == leasedBy
                     && l.LeasedUntil != null
                     && l.LeasedUntil > DateTime.Now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.LeasedUntil, DateTime.Now.AddSeconds(extendSeconds)),
            ct);

        return rows > 0;
    }

    public async Task<IReadOnlySet<int>> GetActivelyLeasedJobIdsAsync(
        IEnumerable<int> jobIds, CancellationToken ct)
    {
        var ids = jobIds.ToList();
        if (ids.Count == 0) return new HashSet<int>();

        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.Now;

        var leased = await db.MonitoredJobLeases
            .Where(l => ids.Contains(l.MonitoredJobId)
                     && l.LeasedUntil != null
                     && l.LeasedUntil > now)
            .Select(l => l.MonitoredJobId)
            .ToListAsync(ct);

        return new HashSet<int>(leased);
    }
}
