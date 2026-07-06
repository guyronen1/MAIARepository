using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

/// <summary>
/// Executes a fix by calling a SQL stored procedure.
/// ActionPayload format:
///   "SpName"                  — uses the default DB connection
///   "ConnectionName|SpName"   — reserved for future per-job connection support
/// The procedure receives @FailureId (int) as a parameter.
/// </summary>
public sealed class StoredProcedureExecutor(
    IDbContextFactory<MaiaDbContext> factory,
    ILogger<StoredProcedureExecutor> logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.StoredProcedure;

    public async Task<FixActionResult> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError("StoredProcedureExecutor: ActionPayload (SP name) is required for Failure {FailureId}",
                recommendation.FailureId);
            return false;
        }

        // Parse "ConnectionName|SpName" or just "SpName"
        var spName = payload.Contains('|')
            ? payload.Split('|', 2)[1].Trim()
            : payload.Trim();

        if (!IsValidIdentifier(spName))
        {
            logger.LogError(
                "StoredProcedureExecutor: Invalid SP name '{SpName}' for Failure {FailureId}",
                spName, recommendation.FailureId);
            return false;
        }

        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);

        try
        {
            await using var db = await factory.CreateDbContextAsync(cts.Token);
            db.Database.SetCommandTimeout((int)ExecutorTimeouts.Default.TotalSeconds);
            var failureIdParam = new SqlParameter("@FailureId", recommendation.FailureId);

            await db.Database.ExecuteSqlRawAsync(
                $"EXEC {spName} @FailureId", new[] { failureIdParam }, cts.Token);

            logger.LogInformation(
                "StoredProcedureExecutor: Executed {SpName} for Failure {FailureId}",
                spName, recommendation.FailureId);
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "StoredProcedureExecutor: {SpName} timed out after {Seconds}s for Failure {FailureId}",
                spName, ExecutorTimeouts.Default.TotalSeconds, recommendation.FailureId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "StoredProcedureExecutor: {SpName} failed for Failure {FailureId}",
                spName, recommendation.FailureId);
            return FixActionResult.Fail(ex.Message);
        }
    }

    // Allows: letters, digits, underscores, dots, brackets (schema.SpName, [dbo].[Sp])
    private static bool IsValidIdentifier(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']');
}
