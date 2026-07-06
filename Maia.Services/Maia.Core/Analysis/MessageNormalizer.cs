using System.Text.RegularExpressions;

namespace Maia.Core.Analysis;

/// <summary>
/// Normalizes a raw <c>JobFailure.ErrorMessage</c> so that n-gram clustering
/// sees the stable signal ("Task failed", "Package execution completed with
/// errors") instead of per-message noise (the scan prefix, timestamps, ids)
/// that would otherwise dominate frequency counts and bury the real patterns.
///
/// Each stage is a separate public method so it's independently unit-testable
/// and so an operator (or a v2 analyzer) can introspect "what did the
/// normalizer do to this message" step by step.
///
/// Pipeline order: scan-prefix → leading-timestamp → GUID → digit-run.
/// NOTE: GUID collapse runs BEFORE digit-run collapse. A GUID contains 4+
/// digit substrings, so collapsing digit runs first would shred GUIDs before
/// the GUID pattern could match. This intentionally swaps the originally
/// specified "digits then GUID" ordering — the two are not commutative.
/// </summary>
public static class MessageNormalizer
{
    // "[<keyword>] <filename>: " — filename may contain spaces (e.g.
    // "app-2026 - Copy.log"), so match non-greedily up to the FIRST ": ".
    // Requires whitespace after the "]" so a content token like
    // "[dbo.Files].[Col]" (DB-scan message, no space after "]") is NOT treated
    // as a scan prefix.
    private static readonly Regex ScanPrefix = new(
        @"^\[[^\]]+\]\s+.+?:\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    // Leading "[YYYY-MM-DD HH:MM:SS]".
    private static readonly Regex LeadingTimestamp = new(
        @"^\[\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\]\s*", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly Regex DigitRun = new(@"\d{4,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <summary>Stage 1 — strip the "[keyword] filename:" scan prefix.</summary>
    public static string StripScanPrefix(string message)
        => ScanPrefix.Replace(message ?? string.Empty, string.Empty);

    /// <summary>Stage 2 — strip a leading "[YYYY-MM-DD HH:MM:SS]" timestamp.</summary>
    public static string StripLeadingTimestamp(string message)
        => LeadingTimestamp.Replace(message ?? string.Empty, string.Empty);

    /// <summary>Stage 3 — collapse GUIDs to the literal "&lt;GUID&gt;".</summary>
    public static string CollapseGuids(string message)
        => GuidPattern.Replace(message ?? string.Empty, "<GUID>");

    /// <summary>Stage 4 — collapse runs of 4+ digits to the literal "&lt;NUM&gt;".</summary>
    public static string CollapseDigitRuns(string message)
        => DigitRun.Replace(message ?? string.Empty, "<NUM>");

    /// <summary>Full pipeline. Returns the introspectable normalized form
    /// (keeps &lt;NUM&gt; / &lt;GUID&gt; placeholders so callers can see what
    /// was masked). Empty string for null/blank input.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = StripScanPrefix(raw);
        s = StripLeadingTimestamp(s);
        s = CollapseGuids(s);
        s = CollapseDigitRuns(s);
        return s.Trim();
    }
}
