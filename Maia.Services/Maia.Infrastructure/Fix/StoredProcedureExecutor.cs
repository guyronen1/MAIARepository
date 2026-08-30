using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

/// <summary>
/// Executes a fix by calling a SQL stored procedure.
///
/// Connection resolution order (same as <see cref="SqlScriptExecutor"/>):
///   1. ActionPayload prefix "ConnectionName|SpName" — explicit override
///   2. The failure's ScanSource.ConnectionName (set by Database scan sources)
///   3. "DefaultConnection" (MaiaDb itself)
///
/// The procedure receives two parameters:
///   @FailureId (int)                — MAIA's internal JobFailure PK. Meaningless as a
///                                      fix target on an external system (see SqlScriptExecutor).
///   @SourceId  (nvarchar, nullable)  — the business-key identifier from the scanned
///                                      source (JobFailure.SourceId), e.g. a Files.Id GUID
///                                      as text. NULL when the scan didn't populate one.
/// The SP name/payload does NOT support {failureId}/{sourceId} placeholder tokens —
/// those are supplied as real SqlParameters above, not string-substituted.
/// </summary>
public sealed class StoredProcedureExecutor(
    IDbContextFactory<MaiaDbContext> factory,
    IConfiguration                   config,
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
            return FixActionResult.Fail("No stored procedure is configured on this fix policy.");
        }

        var (connectionName, spName) = SplitPayload(payload);

        if (!IsValidIdentifier(spName))
        {
            logger.LogError(
                "StoredProcedureExecutor: Invalid SP name '{SpName}' for Failure {FailureId}",
                spName, recommendation.FailureId);
            return FixActionResult.Fail($"'{spName}' is not a valid stored procedure name.");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var failure = await db.JobFailures
            .Include(j => j.ScanSource)
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.FailureId == recommendation.FailureId, ct);

        if (failure is null)
        {
            logger.LogError("StoredProcedureExecutor: Failure {FailureId} not found", recommendation.FailureId);
            return FixActionResult.Fail($"Failure {recommendation.FailureId} not found.");
        }

        connectionName ??= failure.ScanSource?.ConnectionName ?? "DefaultConnection";
        var connStr = config.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            logger.LogError(
                "StoredProcedureExecutor: Connection '{ConnectionName}' not found in configuration (Failure {FailureId})",
                connectionName, recommendation.FailureId);
            return FixActionResult.Fail($"Connection '{connectionName}' is not configured on this server.");
        }

        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cts.Token);
            await using var cmd = new SqlCommand(spName, conn)
            {
                CommandType    = System.Data.CommandType.StoredProcedure,
                CommandTimeout = (int)ExecutorTimeouts.Default.TotalSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@FailureId", recommendation.FailureId));
            cmd.Parameters.Add(new SqlParameter("@SourceId", System.Data.SqlDbType.NVarChar, 200)
            {
                Value = (object?)failure.SourceId ?? DBNull.Value,
            });

            await cmd.ExecuteNonQueryAsync(cts.Token);

            logger.LogInformation(
                "StoredProcedureExecutor: Executed {SpName} for Failure {FailureId} on '{ConnectionName}'",
                spName, recommendation.FailureId, connectionName);
            return FixActionResult.Ok($"Executed '{spName}' on '{connectionName}'.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "StoredProcedureExecutor: {SpName} timed out after {Seconds}s for Failure {FailureId} on '{ConnectionName}'",
                spName, ExecutorTimeouts.Default.TotalSeconds, recommendation.FailureId, connectionName);
            return FixActionResult.Fail(
                $"Stored procedure timed out after {ExecutorTimeouts.Default.TotalSeconds:0}s on '{connectionName}'.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "StoredProcedureExecutor: {SpName} failed for Failure {FailureId} on '{ConnectionName}'",
                spName, recommendation.FailureId, connectionName);
            return FixActionResult.Fail(ex.Message);
        }
    }

    private static (string? ConnectionName, string SpName) SplitPayload(string payload)
    {
        var pipe = payload.IndexOf('|');
        if (pipe <= 0) return (null, payload.Trim());
        var name = payload[..pipe].Trim();
        var sp   = payload[(pipe + 1)..].Trim();
        return (string.IsNullOrEmpty(name) ? null : name, sp);
    }

    // Allows: letters, digits, underscores, dots, brackets (schema.SpName, [dbo].[Sp])
    private static bool IsValidIdentifier(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']');
}
