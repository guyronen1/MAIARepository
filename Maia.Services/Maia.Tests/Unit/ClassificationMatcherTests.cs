using Maia.Core.Classification;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Pins the ClassificationRule matching semantics: case-insensitive substring,
/// <c>*</c> wildcard with all other chars literal, and (the reason this exists)
/// whitespace-tolerance so an analyzer-suggested single-spaced pattern matches
/// a log line with irregular spacing.
/// </summary>
public class ClassificationMatcherTests
{
    [Fact]
    public void WhitespaceTolerant_SingleSpacePatternMatchesDoubleSpaceLine()
    {
        // The exact /unconfigured regression: log has "INFO  Package" (double
        // space), suggested pattern is single-spaced.
        var line = "[Error] app.log: [2026-05-01 22:00:06] INFO  Package execution completed with errors.";
        Assert.True(ClassificationMatcher.IsMatch(line, "info package execution completed with errors"));
    }

    [Theory]
    [InlineData("ERROR Task failed: Data Flow Task", "error task failed", true)]   // case-insensitive
    [InlineData("Connection timed out badly", "timed out", true)]                  // substring
    [InlineData("All good here", "task failed", false)]                            // no match
    public void Substring_CaseInsensitive(string line, string pattern, bool expected)
        => Assert.Equal(expected, ClassificationMatcher.IsMatch(line, pattern));

    [Theory]
    [InlineData("Login failed for user sa today", "Login failed for user *", true)]
    [InlineData("Order 5 code 0x80004005 occurred", "code * occurred", true)]
    [InlineData("nothing here", "Login failed for user *", false)]
    public void Wildcard_StarMatchesAnyRun(string line, string pattern, bool expected)
        => Assert.Equal(expected, ClassificationMatcher.IsMatch(line, pattern));

    [Fact]
    public void RegexMetacharsAreLiteral()
    {
        // '.' and '+' in a pattern are literal, not regex.
        Assert.True(ClassificationMatcher.IsMatch("file a.b.c done", "a.b.c"));
        Assert.False(ClassificationMatcher.IsMatch("file axbxc done", "a.b.c"));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    [InlineData("x", "")]
    [InlineData("x", "   ")]   // whitespace-only pattern collapses to empty → no match
    public void EmptyOrNull_NoMatch(string? line, string? pattern)
        => Assert.False(ClassificationMatcher.IsMatch(line, pattern));

    // ── DB-scan "Field=Value" token (DatabaseScanStrategy.BuildRowMessage) ──────
    // A ValueEquals failure message is verbose: "[Table].[Field] = v matches error
    // value v (id=…)". An intuitive literal pattern "Field=Value" can't substring-
    // match it (there's "] = " between field and value). BuildRowMessage appends a
    // compact space-free token "[Field=ExpectedValue]" so the intuitive pattern
    // matches at runtime — and the coverage-marker/flow synthetic keyword (also
    // "Field=Value") genuinely appears in the message. See DECISIONS.

    [Fact]
    public void DbScanValueEquals_TokenlessMessage_DoesNotMatchLiteralPattern()
    {
        // The pre-token message shape — the reason failures went unclassified.
        var msg = "[dbo.Event].[EventStatusCode] = 8 matches error value 8 (id=abc)";
        Assert.False(ClassificationMatcher.IsMatch(msg, "EventStatusCode=8"));
    }

    [Fact]
    public void DbScanValueEquals_WithToken_MatchesLiteralPattern()
    {
        // BuildRowMessage now appends " [EventStatusCode=8]".
        var msg = "[dbo.Event].[EventStatusCode] = 8 matches error value 8 (id=abc) [EventStatusCode=8]";
        Assert.True(ClassificationMatcher.IsMatch(msg, "EventStatusCode=8"));
        Assert.False(ClassificationMatcher.IsMatch(msg, "EventStatusCode=9")); // =9 must not match the =8 message
    }
}
