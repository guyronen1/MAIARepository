using Maia.Core.Analysis;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Pins each normalization stage independently (operators / v2 analyzers can
/// introspect "what did the normalizer do") plus the full pipeline and the
/// load-bearing GUID-before-digits ordering.
/// </summary>
public class MessageNormalizerTests
{
    [Theory]
    [InlineData("[Error] app-20260528.log: [2026-05-01 12:00:05] ERROR Task failed",
                "[2026-05-01 12:00:05] ERROR Task failed")]
    [InlineData("[Exception] app-2026 - Copy.log: payload here",   // filename with spaces
                "payload here")]
    public void StripScanPrefix_RemovesKeywordFilenamePrefix(string input, string expected)
        => Assert.Equal(expected, MessageNormalizer.StripScanPrefix(input));

    [Fact]
    public void StripScanPrefix_LeavesDbScanMessageUntouched()
    {
        // "[dbo.Files].[Col]" has no space after "]" → not a scan prefix.
        const string msg = "[dbo.Files].[NumberOfEntities] = 0 is outside range";
        Assert.Equal(msg, MessageNormalizer.StripScanPrefix(msg));
    }

    [Fact]
    public void StripLeadingTimestamp_RemovesBracketedTimestamp()
        => Assert.Equal("ERROR Task failed",
            MessageNormalizer.StripLeadingTimestamp("[2026-05-01 12:00:05] ERROR Task failed"));

    [Fact]
    public void CollapseGuids_ReplacesGuidWithPlaceholder()
        => Assert.Equal("Id=<GUID> done",
            MessageNormalizer.CollapseGuids("Id=6252C121-AAAA-4433-B79A-0008C7821615 done"));

    [Fact]
    public void CollapseDigitRuns_ReplacesRunsOf4PlusOnly()
        => Assert.Equal("v<NUM> a12 b<NUM>",   // "12" (2 digits) kept; "1234"/"99999" collapsed
            MessageNormalizer.CollapseDigitRuns("v1234 a12 b99999"));

    [Fact]
    public void Normalize_GuidSurvivesAsPlaceholder_NotShreddedByDigitCollapse()
    {
        // The ordering guarantee: a GUID (which contains 4+ digit runs) must
        // come out as a single <GUID>, never partially mangled into <NUM>.
        var result = MessageNormalizer.Normalize(
            "[dbo.Files].[NumberOfEntities] = 0 is outside range [1.0000, 999999.0000] (Id=6252C121-AAAA-4433-B79A-0008C7821615)");
        Assert.Contains("<GUID>", result);
        Assert.DoesNotContain("6252", result);
    }

    [Fact]
    public void Normalize_FullPipeline_LeavesCleanSignal()
    {
        var result = MessageNormalizer.Normalize(
            "[Error] app-20260528.log: [2026-05-01 12:00:05] ERROR Task failed: VERIFY-212920-3333");
        Assert.Equal("ERROR Task failed: VERIFY-<NUM>-<NUM>", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankInput_ReturnsEmpty(string? input)
        => Assert.Equal(string.Empty, MessageNormalizer.Normalize(input));
}
