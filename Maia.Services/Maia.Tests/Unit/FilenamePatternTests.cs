using Maia.Core.Scanning;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Pin the FilenamePattern DSL contract. Same wildcard semantics as
/// classification rules — '*' is the ONLY wildcard, every other character
/// is literal, no-'*' patterns are case-insensitive SUBSTRING match.
/// </summary>
public class FilenamePatternTests
{
    // ── Wildcard mode (pattern contains '*') ─────────────────────────────────

    [Theory]
    [InlineData("*WARNING*.xml",  "log_WARNING_20260601.xml")]
    [InlineData("*WARNING*.xml",  "WARNING.xml")]
    [InlineData("*WARNING*.xml",  "WARNINGabcWARNING.xml")]
    [InlineData("app*.log",       "app_2026.log")]
    [InlineData("app*.log",       "app.log")]                  // '*' matches empty
    [InlineData("*.log",          "x.log")]
    [InlineData("*",              "anything.txt")]             // '*' alone = match everything
    [InlineData("*",              "noextension")]              // including no-extension
    public void Wildcard_Matches(string pattern, string filename)
    {
        Assert.True(FilenamePattern.Matches(filename, pattern),
            $"Expected '{filename}' to match '{pattern}'");
    }

    [Theory]
    [InlineData("*WARNING*.xml",  "log_WARNING.txt")]          // wrong extension
    [InlineData("app*.log",       "myapp.txt")]                // contains "app" but no ".log"
    [InlineData("app*.log",       "ap.log")]                   // missing one 'p'
    [InlineData("*.log",          "x.txt")]                    // no ".log"
    public void Wildcard_NonMatches(string pattern, string filename)
    {
        // NOTE: the DSL is unanchored — same as RuleBasedClassifier.Matches.
        // `app*.log` matches any filename CONTAINING "app", then any text,
        // then ".log" — so "myapp.log" DOES match (substring "app"…"log"),
        // and "MyApp_2026.log" does too. Test cases here are the ones that
        // genuinely shouldn't match.
        Assert.False(FilenamePattern.Matches(filename, pattern));
    }

    [Theory]
    [InlineData("app*.log",  "myapp.log")]   // 'app' substring + '.log' suffix → matches
    [InlineData("app*.log",  "MyApp_2026.log")]
    public void Wildcard_IsUnanchored_LikeClassificationDsl(string pattern, string filename)
    {
        Assert.True(FilenamePattern.Matches(filename, pattern),
            $"Convention is substring-based; '{filename}' DOES match '{pattern}'");
    }

    // ── Literal mode (no '*') = case-insensitive SUBSTRING ──────────────────

    [Theory]
    [InlineData("WARNING",  "log_WARNING.txt")]                // substring hit
    [InlineData("WARNING",  "warning.txt")]                    // case-insensitive
    [InlineData("WARNING",  "WARNING")]                        // exact
    [InlineData("error",    "ERROR_LOG.txt")]                  // case-insensitive
    [InlineData("error",    "error")]                          // exact
    public void Literal_Matches_Substring(string pattern, string filename)
    {
        Assert.True(FilenamePattern.Matches(filename, pattern),
            $"Expected '{filename}' to contain '{pattern}' (case-insensitive)");
    }

    [Fact]
    public void Literal_DotIsLiteral_NotRegexAny()
    {
        // 'error.log' with no '*' must NOT match as a regex pattern (where
        // '.' would mean "any char"). It must match only filenames that
        // CONTAIN the literal text "error.log".
        Assert.True (FilenamePattern.Matches("my-error.log",  "error.log"));
        Assert.True (FilenamePattern.Matches("error.log",     "error.log"));
        Assert.False(FilenamePattern.Matches("errorXlog",     "error.log"));   // '.' is literal
        Assert.False(FilenamePattern.Matches("error_log",     "error.log"));
    }

    // ── Special chars: ?, [, +, etc. are LITERAL, NOT regex / glob ─────────

    [Theory]
    [InlineData("?.log",     "?.log",  true)]                  // literal '?'
    [InlineData("?.log",     "a.log",  false)]                 // '?' is NOT single-char wildcard
    [InlineData("?.log",     "b.log",  false)]
    [InlineData("[abc].log", "[abc].log", true)]               // literal '['
    [InlineData("[abc].log", "a.log",     false)]              // not a char class
    [InlineData("file+v2",   "file+v2.txt", true)]             // literal '+'
    [InlineData("file+v2",   "filev2.txt",  false)]            // '+' is NOT regex "one or more"
    public void SpecialChars_Treated_AsLiteral(string pattern, string filename, bool shouldMatch)
    {
        Assert.Equal(shouldMatch, FilenamePattern.Matches(filename, pattern));
    }

    // ── Empty / null edges ──────────────────────────────────────────────────

    [Theory]
    [InlineData("",   "anything")]
    [InlineData("  ", "anything")]
    public void EmptyOrWhitespacePattern_ReturnsFalse(string pattern, string filename)
    {
        Assert.False(FilenamePattern.Matches(filename, pattern));
    }

    [Theory]
    [InlineData(null,  "anything")]
    public void NullPattern_ReturnsFalse(string? pattern, string filename)
    {
        Assert.False(FilenamePattern.Matches(filename, pattern!));
    }

    [Theory]
    [InlineData("anything", "")]
    [InlineData("anything", null)]
    public void EmptyOrNullFilename_ReturnsFalse(string pattern, string? filename)
    {
        Assert.False(FilenamePattern.Matches(filename!, pattern));
    }

    // ── Cross-platform case-insensitivity ───────────────────────────────────
    // The DSL must NOT depend on filesystem case-sensitivity (which is
    // OS-dependent). Both modes treat case the same way.

    [Fact]
    public void Wildcard_IsCaseInsensitive()
    {
        Assert.True(FilenamePattern.Matches("ERROR_LOG.TXT", "*error*.txt"));
        Assert.True(FilenamePattern.Matches("error_log.txt", "*ERROR*.TXT"));
    }

    [Fact]
    public void Literal_IsCaseInsensitive()
    {
        Assert.True(FilenamePattern.Matches("LOG_ERROR_2026.txt", "error"));
        Assert.True(FilenamePattern.Matches("log_error_2026.txt", "ERROR"));
    }
}
