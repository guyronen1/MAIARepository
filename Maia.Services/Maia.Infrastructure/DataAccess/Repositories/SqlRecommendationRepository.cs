using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlRecommendationRepository(IDbContextFactory<MaiaDbContext> factory)
    : IRecommendationRepository
{
    // Atomic claim SQL — mirrors SqlMonitoredJobLeaseRepository's pattern.
    // READPAST = skip locked rows (no blocking between concurrent drains);
    // UPDLOCK + ROWLOCK = take a write-intent lock on the row we update;
    // TOP(@batchSize) bounds the claim; OUTPUT returns just the claimed ids
    // so we can do a second load with the Failure include (EF can't easily
    // do an UPDATE-with-OUTPUT-with-includes in one query, so two queries
    // it is — both indexed lookups, sub-millisecond each).
    //
    // Filter excludes failures already past Failed status (Resolved /
    // ManualRequired / AwaitingManualAction) so a failed executor that
    // moved the failure to ManualRequired doesn't re-pull the same rec
    // every tick. Closes the pre-existing infinite-retry bug.
    private const string ClaimSql = """
        UPDATE TOP(@batchSize) r
        SET    r.ClaimedBy = @claimedBy,
               r.ClaimedAt = SYSDATETIME()
        OUTPUT inserted.RecommendationId
        FROM   AIRecommendations r WITH (READPAST, UPDLOCK, ROWLOCK)
        JOIN   JobFailures f ON f.FailureId = r.FailureId
        WHERE  r.IsExecuted = 0
          AND  (r.OperatorApproved = 1 OR r.AutoFixAvailable = 1)
          AND  f.Status = 'Failed'
          AND  (r.ClaimedBy IS NULL OR r.ClaimedAt < @claimExpiry);
        """;

    public async Task<List<AiRecommendation>> ClaimPendingAsync(
        string claimedBy, int batchSize, TimeSpan claimTimeout, CancellationToken ct = default)
    {
        if (batchSize <= 0) return [];
        var claimExpiry = DateTime.Now - claimTimeout;

        await using var db = await factory.CreateDbContextAsync(ct);

        // Phase 1: atomic claim → list of RecommendationIds.
        var claimedIds = new List<int>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using (var cmd = (SqlCommand)conn.CreateCommand())
        {
            cmd.CommandText = ClaimSql;
            cmd.Parameters.AddWithValue("@batchSize",   batchSize);
            cmd.Parameters.AddWithValue("@claimedBy",   claimedBy);
            cmd.Parameters.AddWithValue("@claimExpiry", claimExpiry);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                claimedIds.Add(reader.GetInt32(0));
        }

        if (claimedIds.Count == 0) return [];

        // Phase 2: load the claimed recs with Failure include (so the engine
        // can do the policy lookup without a per-rec round-trip).
        return await db.AIRecommendations
            .Include(r => r.Failure)
            .Where(r => claimedIds.Contains(r.RecommendationId))
            .ToListAsync(ct);
    }

    public async Task ReleaseClaimAsync(int recommendationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.AIRecommendations
            .Where(r => r.RecommendationId == recommendationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ClaimedBy, (string?)null)
                .SetProperty(r => r.ClaimedAt, (DateTime?)null), ct);
    }

    public async Task SaveAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AIRecommendations.Add(recommendation);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkExecutedAsync(int recommendationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Use ExecuteUpdateAsync (no tracked entity load) so the claim
        // clear + IsExecuted set happen in one round-trip.
        await db.AIRecommendations
            .Where(r => r.RecommendationId == recommendationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsExecuted, true)
                .SetProperty(r => r.ClaimedBy,  (string?)null)
                .SetProperty(r => r.ClaimedAt,  (DateTime?)null), ct);
    }

    public async Task<bool> SetApprovalAsync(int recommendationId, bool approved, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AIRecommendations
            .Where(r => r.RecommendationId == recommendationId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OperatorApproved, approved), ct);
        return rows > 0;
    }

    public async Task<bool> ExistsForFailureAsync(int failureId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AIRecommendations.AnyAsync(r => r.FailureId == failureId, ct);
    }

    public async Task<PagedResult<RecommendationListItem>> GetPagedAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.AIRecommendations
            .Include(r => r.Failure)
            .Include(r => r.ErrorType)
            .OrderByDescending(r => r.RecommendedAt);

        var total = await query.CountAsync(ct);

        // Two correlated subqueries — override layer first, default layer
        // second. Project both, then coalesce in memory after the materialise
        // (LINQ's null-coalescing on subquery results doesn't always translate
        // cleanly with .Include + .Skip + .Take on top, so the safer shape is
        // "fetch both, decide in memory"). Both subqueries are guarded by the
        // filtered unique indexes so each returns at most one row — the
        // OrderByDescending is just a defensive tiebreaker. Mirrors
        // SqlFixPolicyRepository.GetForAsync priority exactly so the UI shows
        // the policy that will actually be used at execution time.
        var raw = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                Rec      = r,
                Override = r.Failure!.MonitoredJobId == null ? null : db.FixPolicyRules
                    .Where(p => p.Enabled
                             && p.ErrorTypeId    == r.ErrorTypeId
                             && p.MonitoredJobId == r.Failure!.MonitoredJobId)
                    .OrderByDescending(p => p.ActionTimestamp)
                    .Select(p => new { p.RuleId, p.IsAutoHealEligible, StepCount = p.Steps.Count, p.ActionType })
                    .FirstOrDefault(),
                Default  = db.FixPolicyRules
                    .Where(p => p.Enabled
                             && p.ErrorTypeId    == r.ErrorTypeId
                             && p.JobTypeId      == r.Failure!.JobTypeId
                             && p.MonitoredJobId == null)
                    .OrderByDescending(p => p.ActionTimestamp)
                    .Select(p => new { p.RuleId, p.IsAutoHealEligible, StepCount = p.Steps.Count, p.ActionType })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = raw
            .Select(x =>
            {
                // Override-then-default priority — must match
                // SqlFixPolicyRepository.GetForAsync exactly so the UI shows
                // the policy that will actually be used at execution time.
                var policy = x.Override ?? x.Default;
                return new RecommendationListItem(
                    x.Rec,
                    policy?.RuleId,
                    policy?.IsAutoHealEligible,
                    policy?.StepCount ?? 0,
                    policy?.ActionType.ToString());
            })
            .ToList();

        return new PagedResult<RecommendationListItem>(items, total, page, pageSize);
    }
}
