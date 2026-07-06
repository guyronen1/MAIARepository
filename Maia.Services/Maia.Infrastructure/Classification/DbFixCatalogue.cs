using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Classification;

/// <summary>
/// DB-driven IFixCatalogue: reads FixPolicyRules from the database.
/// Falls back to built-in defaults when no DB rule exists for the error code.
/// This lets operators configure fix actions at runtime without redeployment.
/// </summary>
public sealed class DbFixCatalogue(
    IFixCatalogueRepository catalogueRepo,
    ILogger<DbFixCatalogue> logger) : IFixCatalogue
{
    private static readonly Dictionary<string, FixCatalogueEntry> Defaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FileNotFound"] = new("Verify source file path and re-queue the job.",          FixCategory.FileRepair, 0.0,  false),
            ["DbConnection"] = new("Check SQL Server connectivity and retry.",               FixCategory.Retry,      0.0,  true),
            ["Timeout"]      = new("Increase command timeout in DTSX config and retry.",    FixCategory.Retry,      0.0,  true),
            ["Transform"]    = new("Inspect DTSX component error; check column mappings.",  FixCategory.Manual,    -0.1,  false),
            ["Permission"]   = new("Verify service account permissions on the resource.",   FixCategory.Manual,    -0.1,  false),
            ["Unknown"]      = new("Manual investigation required.",                         FixCategory.Manual,    -0.2,  false),
        };

    public async Task<FixCatalogueEntry?> GetEntryAsync(
        string errorTypeCode,
        int    jobTypeId,
        int?   monitoredJobId = null,
        CancellationToken ct = default)
    {
        // 1. Try the DB for an override (per-MonitoredJob) or default
        // (per-JobType) match — the repo handles priority internally.
        var dbEntry = await catalogueRepo.GetEntryAsync(errorTypeCode, jobTypeId, monitoredJobId, ct);
        if (dbEntry is not null)
        {
            logger.LogDebug(
                "FixCatalogue: DB entry used for ({ErrorTypeCode}, jobTypeId={JobTypeId}, monitoredJobId={MonitoredJobId})",
                errorTypeCode, jobTypeId, monitoredJobId);
            return dbEntry;
        }

        // 2. Last-resort: ErrorType-only static defaults. Operators who need
        // JobType- or MonitoredJob-specific behavior must add a FixPolicyRule.
        if (Defaults.TryGetValue(errorTypeCode, out var fallback))
        {
            logger.LogDebug(
                "FixCatalogue: default entry used for {ErrorTypeCode} (no FixPolicyRule for jobTypeId={JobTypeId}, monitoredJobId={MonitoredJobId})",
                errorTypeCode, jobTypeId, monitoredJobId);
            return fallback;
        }

        return null;
    }
}
