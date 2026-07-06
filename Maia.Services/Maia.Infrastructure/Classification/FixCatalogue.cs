using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;

namespace Maia.Infrastructure.Classification;

/// <summary>
/// Static in-memory fallback catalogue — used in tests or when no DB policy rules exist.
/// For production, prefer DbFixCatalogue which reads from FixPolicyRules and falls back here.
/// </summary>
public sealed class FixCatalogue : IFixCatalogue
{
    private static readonly Dictionary<string, FixCatalogueEntry> Entries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FileNotFound"] = new("Verify source file path and re-queue the job.",          FixCategory.FileRepair, 0.0,  false),
            ["DbConnection"] = new("Check SQL Server connectivity and retry.",               FixCategory.Retry,      0.0,  true),
            ["Timeout"]      = new("Increase command timeout in DTSX config and retry.",    FixCategory.Retry,      0.0,  true),
            ["Transform"]    = new("Inspect DTSX component error; check column mappings.",  FixCategory.Manual,    -0.1,  false),
            ["Permission"]   = new("Verify service account permissions on the resource.",   FixCategory.Manual,    -0.1,  false),
            ["Unknown"]      = new("Manual investigation required.",                         FixCategory.Manual,    -0.2,  false),
        };

    public Task<FixCatalogueEntry?> GetEntryAsync(
        string errorTypeCode,
        int    jobTypeId,
        int?   monitoredJobId = null,
        CancellationToken ct = default)
        // In-memory fallback intentionally ignores both jobTypeId and
        // monitoredJobId — last-resort defaults are per-ErrorType only.
        // Operators who need scope-specific behavior should configure a
        // FixPolicyRule row (DbFixCatalogue queries it first with the
        // override-then-default priority).
        => Task.FromResult(Entries.TryGetValue(errorTypeCode, out var entry) ? entry : null);
}
