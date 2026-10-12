using Maia.Core.Entities;
using Maia.Core.Enums;

namespace Maia.Core.Interfaces;

/// <summary>
/// Outcome of a "start scanning from now" watermark seed.
/// <para><see cref="Applicable"/> = false means the rule/source kind has no
/// watermark concept (ApiEndpoint, a DB rule without WatermarkColumn) — nothing
/// was attempted and no warning is due. <see cref="Warning"/> is non-null when
/// seeding was attempted but could not complete (unreachable source DB, missing
/// folder, …); the caller still saves the rule/source — a failed seed just means
/// the first scan will process the full existing history, which is today's
/// behaviour, not an error state.</para>
/// </summary>
public sealed record WatermarkSeedResult(
    bool    Applicable,
    int     Seeded,
    int     Skipped,
    string  Detail,
    string? Warning = null)
{
    public static WatermarkSeedResult NotApplicable(string reason) => new(false, 0, 0, reason);
    public static WatermarkSeedResult Failed(string warning)       => new(true, 0, 0, warning, warning);
}

/// <summary>
/// Seeds watermark rows at the *current* position so a freshly created scan rule /
/// scan source only picks up data that arrives after it was saved, instead of the
/// default scan-everything first tick. Insert-only: never moves an existing
/// watermark (see <c>IScanWatermarkRepository.TryAdd*</c>).
/// </summary>
public interface IScanWatermarkSeeder
{
    /// <summary>DB rules (ColumnRange / ValueEquals / SqlQuery) with a WatermarkColumn:
    /// seeds <c>ScanDbWatermarks</c> with the current MAX of the watermark column over
    /// the rows the rule would report on. File-based rules (ErrorKeyword / FileContent)
    /// have no per-rule watermark and delegate to <see cref="SeedSourceFromNowAsync"/>
    /// for their owning source. Not applicable for a DB rule without WatermarkColumn.</summary>
    Task<WatermarkSeedResult> SeedRuleFromNowAsync(ScanCheckRule rule, ScanSource source, CancellationToken ct = default);

    /// <summary>FileSystem sources: seeds <c>ScanFileWatermarks</c> at each currently
    /// matching file's byte length. FileContent sources: seeds
    /// <c>ScanContentWatermarks</c> with each file's current last-write time.
    /// Not applicable for Database / ApiEndpoint sources.</summary>
    Task<WatermarkSeedResult> SeedSourceFromNowAsync(ScanSource source, ScanType scanType, CancellationToken ct = default);
}
