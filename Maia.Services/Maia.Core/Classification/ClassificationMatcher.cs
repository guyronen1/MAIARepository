using System.Text.RegularExpressions;

namespace Maia.Core.Classification;

/// <summary>
/// Matches a log line against a ClassificationRule pattern. Public + pure so
/// it's unit-testable and reusable (sibling to <c>Core/Scanning/FilenamePattern</c>
/// for the FS filename DSL).
///
/// Semantics:
///   • Case-insensitive.
///   • <c>*</c> is the only wildcard — matches any run of characters (incl.
///     none). Every other character (including regex metachars <c>.</c>,
///     <c>+</c>, <c>[</c>) is literal.
///   • WHITESPACE-TOLERANT — runs of whitespace collapse to a single space on
///     BOTH the line and the pattern before matching. Logs have irregular
///     spacing (e.g. <c>"INFO  Package"</c> with a double space), and the
///     /unconfigured cluster analyzer emits single-spaced suggested patterns;
///     without this a correct-looking pattern silently fails to match its own
///     source. Strictly more permissive on whitespace only.
///   • 50ms regex timeout (ReDoS defence-in-depth; the construction is already
///     safe — no nested quantifiers / backreferences).
/// </summary>
public static class ClassificationMatcher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex WhitespaceRuns = new(@"\s+", RegexOptions.Compiled, Timeout);

    public static bool IsMatch(string? line, string? pattern)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(pattern))
            return false;

        var hay = WhitespaceRuns.Replace(line, " ");
        var pat = WhitespaceRuns.Replace(pattern, " ").Trim();
        if (pat.Length == 0)
            return false;

        if (!pat.Contains('*'))
            return hay.Contains(pat, StringComparison.OrdinalIgnoreCase);

        var regex = string.Join(".*", pat.Split('*').Select(Regex.Escape));
        try
        {
            return Regex.IsMatch(hay, regex, RegexOptions.IgnoreCase, Timeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
