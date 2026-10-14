using System.Globalization;
using Maia.Core.Enums;

namespace Maia.Core.Scanning;

/// <summary>
/// The check kinds a Database <b>table</b> rule (and each of its extra conditions)
/// can use, and what each one compares against. One place so the scan strategy,
/// the config controller, the watermark seeder and View Source agree.
/// <list type="bullet">
///   <item><c>ColumnRange</c> — column outside [MinValue, MaxValue].</item>
///   <item><c>ValueEquals</c> — <c>col = ExpectedValue</c>.</item>
///   <item><c>NotEquals</c> — <c>col &lt;&gt; ExpectedValue</c>; NULL rows are NOT flagged
///     (plain SQL three-valued logic, decided 2026-10-14).</item>
///   <item><c>Contains</c> — <c>col LIKE '%ExpectedValue%'</c>, the text matched literally.</item>
///   <item><c>InList</c> — <c>col IN (…)</c>; ExpectedValue holds the numbers, comma-separated.</item>
/// </list>
/// </summary>
public static class DbCheckKinds
{
    /// <summary>Most numbers an InList check accepts — each one is a bound parameter.</summary>
    public const int MaxInListItems = 100;

    /// <summary>A table-rule check kind (excludes SqlQuery, which has no predicate).</summary>
    public static bool IsTableCheck(CheckType kind) => kind is
        CheckType.ColumnRange or CheckType.ValueEquals or
        CheckType.NotEquals or CheckType.Contains or CheckType.InList;

    /// <summary>Any check kind a Database source runs, SqlQuery included.</summary>
    public static bool IsDatabaseCheck(CheckType kind) => IsTableCheck(kind) || kind == CheckType.SqlQuery;

    /// <summary>The kind compares against ExpectedValue (everything except ColumnRange).</summary>
    public static bool UsesExpectedValue(CheckType kind) => kind is
        CheckType.ValueEquals or CheckType.NotEquals or CheckType.Contains or CheckType.InList;

    /// <summary>
    /// Parses an InList value ("1, 2, 3") into its numbers. False when it is blank,
    /// has a non-numeric item, or more than <see cref="MaxInListItems"/> items.
    /// Invariant culture, so "1.5" is one number and "1,5" is two.
    /// </summary>
    public static bool TryParseInList(string? csv, out List<decimal> values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(csv)) return false;
        foreach (var item in csv.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!decimal.TryParse(item, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return false;
            values.Add(v);
        }
        return values.Count is > 0 and <= MaxInListItems;
    }

    /// <summary>The canonical stored/displayed form of an InList value: "1, 2, 3".</summary>
    public static string FormatInList(IEnumerable<decimal> values)
        => string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// True when the kind has the value(s) it compares against — the save-time and
    /// scan-time completeness check. ColumnRange needs a bound; the others need
    /// ExpectedValue, and InList needs it to parse.
    /// </summary>
    public static bool HasRequiredValue(CheckType kind, string? expectedValue, decimal? min, decimal? max) => kind switch
    {
        CheckType.ColumnRange => min.HasValue || max.HasValue,
        CheckType.InList      => TryParseInList(expectedValue, out _),
        CheckType.ValueEquals or CheckType.NotEquals or CheckType.Contains => !string.IsNullOrWhiteSpace(expectedValue),
        _ => false,
    };
}
