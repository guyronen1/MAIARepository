namespace Maia.Core.Results;

/// <summary>What <see cref="SourceDataResult"/> carries.</summary>
public static class SourceDataKind
{
    /// <summary>The source row(s) of a Database failure, every column.</summary>
    public const string DbRows = "DbRows";

    /// <summary>The log / data file of a FileSystem or FileContent failure.</summary>
    public const string File = "File";

    /// <summary>The source can't be shown; <see cref="SourceDataResult.Reason"/> says why.</summary>
    public const string Unavailable = "Unavailable";
}

/// <summary>One column of a source row. <c>Value</c> null = SQL NULL.</summary>
public sealed record SourceColumn(string Name, string? Value, bool Truncated = false);

/// <summary>
/// The live source data behind a failure, read on demand for the "View Source"
/// popup (see <c>ISourceDataReader</c>). Live, not a snapshot: the row or file may
/// have changed since detection. <see cref="Kind"/> picks which fields are set.
/// </summary>
public sealed class SourceDataResult
{
    public required string Kind     { get; init; }
    public required string ScanType { get; init; }

    /// <summary>Unavailable: why, in operator language. DbRows: an optional note
    /// (e.g. the key matched more than one row).</summary>
    public string? Reason { get; init; }

    // ── DbRows ────────────────────────────────────────────────────────────────
    public string? ConnectionName { get; init; }
    public string? Provider       { get; init; }
    /// <summary>Table name, or "query" for a SqlQuery rule.</summary>
    public string? Table          { get; init; }
    public string? KeyColumn      { get; init; }
    public string? KeyValue       { get; init; }
    public IReadOnlyList<IReadOnlyList<SourceColumn>> Rows { get; init; } = [];
    /// <summary>Columns the rule checks (target, conditions, source id) — highlighted.</summary>
    public IReadOnlyList<string> HighlightColumns { get; init; } = [];

    // ── File ──────────────────────────────────────────────────────────────────
    public string?   Path         { get; init; }
    public long?     SizeBytes    { get; init; }
    public DateTime? LastModified { get; init; }
    public string?   Content      { get; init; }
    /// <summary>True when <see cref="Content"/> is a window of a larger file.</summary>
    public bool      Truncated    { get; init; }
    /// <summary>1-based file line number of the first line of <see cref="Content"/>.</summary>
    public int       WindowStartLine { get; init; } = 1;
    /// <summary>1-based file line numbers holding the matched text.</summary>
    public IReadOnlyList<int> MatchLines { get; init; } = [];
    /// <summary>"xml" (pretty-printed) or "text".</summary>
    public string?   Format       { get; init; }

    public static SourceDataResult Unavailable(string scanType, string reason, string? path = null) => new()
    {
        Kind     = SourceDataKind.Unavailable,
        ScanType = scanType,
        Reason   = reason,
        Path     = path,
    };
}
