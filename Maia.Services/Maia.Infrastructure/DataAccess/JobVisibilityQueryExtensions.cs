using System.Linq.Expressions;
using Maia.Core.Security;

namespace Maia.Infrastructure.DataAccess;

/// <summary>
/// SQL-side half of per-user job visibility (see <see cref="JobVisibility"/>).
/// </summary>
public static class JobVisibilityQueryExtensions
{
    /// <summary>
    /// Keep only rows whose job (selected by <paramref name="jobId"/>) the user may see.
    /// No-op when <paramref name="visibility"/> is null or unrestricted. Otherwise adds a
    /// correlated <c>EXISTS (SELECT 1 FROM UserMonitoredJobs WHERE UserId = @u AND
    /// MonitoredJobId = &lt;row's job&gt;)</c> — an ordinary subquery (safe under the
    /// no-ROW_NUMBER rule) that needs no id-list parameter. A null job (orphan failure)
    /// never matches, so restricted users don't see orphans.
    /// </summary>
    /// <example><c>db.JobFailures.WhereJobVisible(db, vis, f => f.MonitoredJobId)</c>,
    /// <c>db.AIRecommendations.WhereJobVisible(db, vis, r => r.Failure!.MonitoredJobId)</c></example>
    public static IQueryable<T> WhereJobVisible<T>(
        this IQueryable<T> source,
        MaiaDbContext db,
        JobVisibility? visibility,
        Expression<Func<T, int?>> jobId)
    {
        if (visibility is null || visibility.Unrestricted) return source;

        var userId = visibility.UserId;
        Expression<Func<int?, bool>> assigned = id =>
            db.UserMonitoredJobs.Any(a => a.UserId == userId && a.MonitoredJobId == id);

        // Splice the caller's selector body in place of the template's `id` parameter.
        var body = new ReplaceParameter(assigned.Parameters[0], jobId.Body).Visit(assigned.Body);
        return source.Where(Expression.Lambda<Func<T, bool>>(body, jobId.Parameters[0]));
    }

    private sealed class ReplaceParameter(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
