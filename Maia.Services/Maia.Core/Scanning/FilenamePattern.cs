using System.Text.RegularExpressions;

namespace Maia.Core.Scanning;

/// <summary>
/// Filename matching for FileSystem scan jobs. Same wildcard DSL the
/// classification rules use (see <c>RuleBasedClassifier.Matches</c>):
///
/// <list type="bullet">
///   <item><c>*</c> is the ONLY supported wildcard — matches any sequence
///     of characters including the empty string.</item>
///   <item>Every other character is literal — <c>.</c>, <c>?</c>, <c>[</c>,
///     <c>+</c>, etc. match themselves. NO regex interpretation.</item>
///   <item>Case-insensitive cross-platform — does NOT depend on the
///     filesystem's case sensitivity (which is OS-dependent).</item>
///   <item>Patterns without <c>*</c> are case-insensitive SUBSTRING match
///     against the filename. <c>WARNING</c> matches "log_WARNING.txt".</item>
///   <item>Empty / whitespace pattern: returns false (caller should log
///     and skip the rule, not crash).</item>
/// </list>
///
/// Matches against <see cref="Path.GetFileName(string?)"/> only — never
/// the full path. ReDoS-safe by construction (no nested quantifiers, no
/// backreferences); 50ms regex timeout as defence in depth, matching the
/// classification-pattern hardening.
///
/// Deliberately NOT supported (would be a v2 conversation):
///   <c>?</c> (single-char wildcard), <c>[abc]</c> (char class),
///   recursive globs <c>**</c>, regex as a pattern mode.
/// </summary>
public static class FilenamePattern
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Tests whether <paramref name="filename"/> matches <paramref name="pattern"/>
    /// under the wildcard DSL. <paramref name="filename"/> should already be
    /// the file's basename (caller responsibility — typically
    /// <c>Path.GetFileName(fullPath)</c>).
    /// </summary>
    public static bool Matches(string filename, string pattern)
    {
        if (string.IsNullOrEmpty(filename))      return false;
        if (string.IsNullOrWhiteSpace(pattern))  return false;

        // No wildcard → fast case-insensitive substring path. Avoids a
        // regex compile for the common operator pattern "ERROR".
        if (!pattern.Contains('*'))
            return filename.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        // Wildcard mode: escape every non-* character (so `.`, `?`, `[` etc.
        // are literal) and join the segments with `.*`. Compile-and-match on
        // every call is acceptable in practice since the cache lives at the
        // caller (scan strategies cache per-rule across files in one tick).
        var regex = string.Join(".*", pattern.Split('*').Select(Regex.Escape));

        try
        {
            return Regex.IsMatch(filename, regex, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pattern that runs away on a pathological filename — treat as
            // non-match. Operator's regex is unusable; surface via the
            // caller's per-rule logging if they want to know.
            return false;
        }
    }
}
