using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Maia.Core.Scanning;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.Fix;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// "View Source" — see <see cref="ISourceDataReader"/>. Reads LIVE data, so the row or
/// file may have changed since detection (the popup says so).
///  • Database table rules → <c>SELECT * … WHERE SourceIdColumn = @id</c> (at most
///    <see cref="MaxDbRows"/> rows). The rule's SourceIdColumn is the only stable key —
///    without it the failure's SourceId is a one-off ROW_NUMBER and the row can't be
///    found again.
///  • Database SqlQuery rules → the rule's query is re-run (same 500-row cap as the
///    scan) and the rows whose SourceIdColumn equals the SourceId are kept.
///  • FileSystem → the log file at SourceLogPath; the matched line is found again by
///    the excerpt the scan stored in ErrorMessage (no line number is stored).
///  • FileContent → the data file; XML is pretty-printed when it parses.
/// Files must sit under the source's LogFolder — the path comes from the stored
/// failure, never the caller, but a source whose folder was changed since detection
/// must not turn an old failure into a read of some other folder.
/// </summary>
public sealed class SourceDataReader(
    IDbContextFactory<MaiaDbContext> dbFactory,
    IConfiguration                   config,
    ISqlQueryRunner                  sqlRunner,
    ILogger<SourceDataReader>        logger) : ISourceDataReader
{
    internal const int MaxDbRows          = 5;
    internal const int MaxValueLength     = 4000;
    internal const int MaxSqlQueryRows    = 500;     // same cap as DatabaseScanStrategy
    internal const int DefaultMaxFileBytes = 1_048_576;
    internal const int MaxLineLength      = 20_000;
    private  const int MaxMatchLines      = 1000;

    public async Task<SourceDataResult?> ReadAsync(int failureId, CancellationToken ct = default)
    {
        JobFailure? f;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            f = await db.JobFailures.AsNoTracking()
                .Include(j => j.ScanSource!).ThenInclude(s => s.ScanTypeDefinition)
                .Include(j => j.ScanSource!).ThenInclude(s => s.ScanCheckRules).ThenInclude(r => r.Conditions)
                .AsSplitQuery()
                .FirstOrDefaultAsync(j => j.FailureId == failureId, ct);
        }
        if (f is null) return null;

        var source = f.ScanSource;
        if (source is null)
            return SourceDataResult.Unavailable("Unknown", "The scan source that detected this failure no longer exists.");

        var scanType = source.ScanType;
        try
        {
            return scanType switch
            {
                ScanType.Database    => await ReadDatabaseAsync(f, source, ct),
                ScanType.FileSystem  => await ReadFileAsync(f, source, scanType, MaxFileBytes(), ct),
                ScanType.FileContent => await ReadFileAsync(f, source, scanType, MaxFileBytes(), ct),
                _ => SourceDataResult.Unavailable(scanType.ToString(),
                        "API endpoint failures have no stored source data to open." +
                        (string.IsNullOrWhiteSpace(source.LogSourceUrl) ? "" : $" Endpoint: {source.LogSourceUrl}")),
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "View Source failed for failure {FailureId} ({ScanType})", failureId, scanType);
            var reason = ex is OperationCanceledException
                ? $"Reading the source timed out after {ExecutorTimeouts.Default.TotalSeconds:0}s."
                : $"Could not read the source: {ex.Message}";
            return SourceDataResult.Unavailable(scanType.ToString(), reason);
        }
    }

    private int MaxFileBytes()
        => int.TryParse(config["SourceViewer:MaxBytes"], out var n) && n > 0 ? n : DefaultMaxFileBytes;

    // ── Rule resolution ────────────────────────────────────────────────────────

    /// <summary>
    /// The DB rule that produced <paramref name="f"/>: by <see cref="JobFailure.ScanCheckRuleId"/>,
    /// else (failures from before the column existed, or a deleted-and-recreated rule)
    /// by the StepName the scan wrote — the table name for table rules, the
    /// Description / "SqlQuery #id" label for SqlQuery rules. A candidate with a
    /// SourceIdColumn wins, since only that one can locate the row.
    /// </summary>
    internal static ScanCheckRule? ResolveDbRule(JobFailure f, IEnumerable<ScanCheckRule> rules)
    {
        var dbRules = rules
            .Where(r => DbCheckKinds.IsDatabaseCheck(r.CheckType))
            .ToList();

        if (f.ScanCheckRuleId is { } id && dbRules.FirstOrDefault(r => r.CheckRuleId == id) is { } byId)
            return byId;

        var byStep = dbRules.Where(r => string.Equals(StepNameOf(r), f.StepName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byStep.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SourceIdColumn)) ?? byStep.FirstOrDefault();
    }

    // Mirrors DatabaseScanStrategy's stepName.
    private static string? StepNameOf(ScanCheckRule r) => r.CheckType == CheckType.SqlQuery
        ? (string.IsNullOrWhiteSpace(r.Description) ? $"SqlQuery #{r.CheckRuleId}" : r.Description)
        : r.SourceTable;

    // ── Database ───────────────────────────────────────────────────────────────

    private async Task<SourceDataResult> ReadDatabaseAsync(JobFailure f, ScanSource source, CancellationToken ct)
    {
        const string st = nameof(ScanType.Database);

        var rule = ResolveDbRule(f, source.ScanCheckRules);
        if (rule is null)
            return SourceDataResult.Unavailable(st,
                "The scan rule that detected this failure no longer exists, so its source row can't be located.");
        if (string.IsNullOrWhiteSpace(rule.SourceIdColumn))
            return SourceDataResult.Unavailable(st,
                "The scan rule has no Source ID Column, so MAIA can't find this row again. " +
                "Set the table's key column as the rule's Source ID Column — failures detected after that can be opened.");
        if (string.IsNullOrWhiteSpace(f.SourceId))
            return SourceDataResult.Unavailable(st, "This failure has no Source ID recorded.");
        if (string.IsNullOrWhiteSpace(rule.SourceTable))
            return SourceDataResult.Unavailable(st, "The scan rule has no Source Table / Query.");

        var isQuery = rule.CheckType == CheckType.SqlQuery;
        if (!isQuery && rule.SourceTable.Trim().Any(char.IsWhiteSpace))
            return SourceDataResult.Unavailable(st,
                "The rule's Source Table is an expression (e.g. a JOIN), not a single table, so the row can't be looked up directly.");

        var connName   = source.ConnectionName ?? "DefaultConnection";
        var connection = DbConnections.TryResolve(config, connName, out var connError);
        if (connection is null)
            return SourceDataResult.Unavailable(st, connError!);

        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);
        var keyColumn = rule.SourceIdColumn.Trim();
        var rows = isQuery
            ? await QueryRowsByKeyAsync(connection, rule.SourceTable, keyColumn, f.SourceId, cts.Token)
            : await TableRowsByKeyAsync(connection, rule.SourceTable.Trim(), keyColumn, f.SourceId, cts.Token);

        if (rows.Count == 0)
            return SourceDataResult.Unavailable(st, isQuery
                ? $"No row with {keyColumn} = '{f.SourceId}' in the rule's query result any more (the query returns current rows only, at most {MaxSqlQueryRows})."
                : $"No row with {keyColumn} = '{f.SourceId}' in {rule.SourceTable.Trim()} — it may have been deleted or its key changed.");

        return new SourceDataResult
        {
            Kind             = SourceDataKind.DbRows,
            ScanType         = st,
            ConnectionName   = connName,
            Provider         = connection.Provider.ToString(),
            Table            = isQuery ? "query" : rule.SourceTable.Trim(),
            KeyColumn        = keyColumn,
            KeyValue         = f.SourceId,
            Rows             = rows,
            HighlightColumns = HighlightColumnsOf(rule),
            Reason           = rows.Count > 1
                ? $"{keyColumn} is not unique — {(rows.Count >= MaxDbRows ? $"at least {MaxDbRows}" : rows.Count.ToString())} rows match."
                : null,
        };
    }

    /// <summary>The columns the rule checks — target, conditions, key — without any "alias." prefix.</summary>
    internal static IReadOnlyList<string> HighlightColumnsOf(ScanCheckRule rule)
    {
        static string Bare(string c) { var t = c.Trim(); var dot = t.LastIndexOf('.'); return dot < 0 ? t : t[(dot + 1)..]; }
        return new[] { rule.TargetField }
            .Concat(rule.Conditions.Select(c => c.TargetField))
            .Append(rule.SourceIdColumn)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Bare(c!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The lookup SQL. <paramref name="asText"/> = compare the key's text form (the same
    /// projection the scan stored as SourceId) instead of the native column — the
    /// fallback for keys whose text doesn't convert back exactly (e.g. dates).
    /// </summary>
    internal static string KeyLookupSql(SqlDialect d, string table, string keyColumn, bool asText)
    {
        var key = d.QuoteIdentifier(keyColumn);
        if (asText) key = d.IdText(key, 100);
        return d.SelectAllWhereQuery(d.QuoteTable(table), $"{key} = {d.Placeholder("Id")}", MaxDbRows);
    }

    private static async Task<List<IReadOnlyList<SourceColumn>>> TableRowsByKeyAsync(
        ResolvedConnection connection, string table, string keyColumn, string sourceId, CancellationToken ct)
    {
        var d = SqlDialect.For(connection.Provider);
        await using var conn = await DbConnections.OpenAsync(connection, ct);

        // Native compare first — the engine converts the text bind to the key's type,
        // so an indexed int key stays a seek. A conversion error or no hit retries on
        // the text form, which is what the scan stored.
        List<IReadOnlyList<SourceColumn>>? rows = null;
        try { rows = await RunAsync(conn, KeyLookupSql(d, table, keyColumn, asText: false), d, sourceId, ct); }
        catch (DbException) { /* e.g. 'abc' against an int key — fall through to the text compare */ }

        if (rows is { Count: > 0 }) return rows;
        return await RunAsync(conn, KeyLookupSql(d, table, keyColumn, asText: true), d, sourceId, ct);
    }

    private static async Task<List<IReadOnlyList<SourceColumn>>> RunAsync(
        DbConnection conn, string sql, SqlDialect d, string sourceId, CancellationToken ct)
    {
        await using var cmd = DbConnections.CreateCommand(conn, sql, (int)ExecutorTimeouts.Default.TotalSeconds);
        var p = cmd.CreateParameter();
        p.ParameterName = d.ParamName("Id");
        p.Value         = sourceId;
        cmd.Parameters.Add(p);

        var rows = new List<IReadOnlyList<SourceColumn>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (rows.Count < MaxDbRows && await reader.ReadAsync(ct))
        {
            var row = new List<SourceColumn>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                row.Add(Column(reader.GetName(i), DbConnections.ReadValue(reader, i)));
            rows.Add(row);
        }
        return rows;
    }

    private async Task<List<IReadOnlyList<SourceColumn>>> QueryRowsByKeyAsync(
        ResolvedConnection connection, string query, string keyColumn, string sourceId, CancellationToken ct)
    {
        var result = await sqlRunner.ExecuteAsync(connection.Provider, connection.ConnectionString, query, MaxSqlQueryRows, ct);
        return FilterByKey(result, keyColumn, sourceId);
    }

    /// <summary>SqlQuery rows whose key column's text equals <paramref name="sourceId"/>
    /// — the same <c>ToString()</c> the scan used to produce the SourceId.</summary>
    internal static List<IReadOnlyList<SourceColumn>> FilterByKey(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, string keyColumn, string sourceId)
        => rows
            .Where(r => r.TryGetValue(keyColumn, out var v) && v is not null && v.ToString() == sourceId)
            .Take(MaxDbRows)
            .Select(r => (IReadOnlyList<SourceColumn>)r.Select(kv => Column(kv.Key, kv.Value)).ToList())
            .ToList();

    internal static SourceColumn Column(string name, object? value)
    {
        if (value is null) return new SourceColumn(name, null);
        var text = value switch
        {
            string s       => s,
            DateTime dt    => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
            byte[] b       => "0x" + Convert.ToHexString(b.Length > MaxValueLength / 2 ? b[..(MaxValueLength / 2)] : b),
            _              => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        var truncated = text.Length > MaxValueLength || value is byte[] bytes && bytes.Length > MaxValueLength / 2;
        return new SourceColumn(name, text.Length > MaxValueLength ? text[..MaxValueLength] : text, truncated);
    }

    // ── Files ──────────────────────────────────────────────────────────────────

    internal static async Task<SourceDataResult> ReadFileAsync(
        JobFailure f, ScanSource source, ScanType scanType, int maxBytes, CancellationToken ct)
    {
        var st          = scanType.ToString();
        var contentFile = scanType == ScanType.FileContent;
        var path        = contentFile ? (f.SourceFilePath ?? f.SourceLogPath) : f.SourceLogPath;

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return SourceDataResult.Unavailable(st, "No file path was recorded for this failure.");
        if (string.IsNullOrWhiteSpace(source.LogFolder))
            return SourceDataResult.Unavailable(st, "The scan source has no folder configured.", path);

        string full, root;
        try
        {
            full = Path.GetFullPath(path);
            root = Path.GetFullPath(source.LogFolder.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SourceDataResult.Unavailable(st, $"'{path}' is not a valid path.", path);
        }

        if (!PathBoundary.IsUnder(full, root))
            return SourceDataResult.Unavailable(st,
                $"The file is outside the source's folder '{root}' (the source was probably changed since detection), so MAIA won't open it.", full);

        var info = new FileInfo(full);
        if (!info.Exists)
            return SourceDataResult.Unavailable(st, "The file no longer exists — it may have been moved, archived or rolled over.", full);

        var matcher = contentFile ? ContentMatcher(f) : LogLineMatcher(f, source);

        // XML is pretty-printed only when the whole file fits; a window of a big file
        // isn't a parseable document anyway.
        if (contentFile && info.Length <= maxBytes && LooksLikeXml(full))
        {
            var pretty = await TryPrettyXmlAsync(full, ct);
            if (pretty is not null)
                return FileResult(st, info, pretty, startLine: 1, MatchLinesIn(pretty, matcher), truncated: false, format: "xml");
        }

        var window = await ReadTextWindowAsync(full, maxBytes, matcher, ct);
        return FileResult(st, info, window.Content, window.StartLine, window.MatchLines, window.Truncated, "text");
    }

    private static SourceDataResult FileResult(string st, FileInfo info, string content, int startLine,
        IReadOnlyList<int> matchLines, bool truncated, string format) => new()
    {
        Kind            = SourceDataKind.File,
        ScanType        = st,
        Path            = info.FullName,
        SizeBytes       = info.Length,
        LastModified    = info.LastWriteTime,
        Content         = content,
        WindowStartLine = startLine,
        MatchLines      = matchLines,
        Truncated       = truncated,
        Format          = format,
    };

    /// <summary>
    /// FileSystem: the excerpt the scan stored — ErrorMessage is
    /// <c>"[{keyword}] {fileName}: {excerpt}"</c> with excerpt = the trimmed line cut to
    /// 500 chars, so a line matches when its trimmed text starts with it. Falls back to
    /// the rule keyword when the message doesn't have that shape.
    /// </summary>
    internal static Func<string, bool>? LogLineMatcher(JobFailure f, ScanSource source)
    {
        var msg    = f.ErrorMessage ?? string.Empty;
        var marker = $"] {f.StepName}: ";
        var at     = f.StepName is null ? -1 : msg.IndexOf(marker, StringComparison.Ordinal);
        if (msg.StartsWith('[') && at > 0)
        {
            var excerpt = msg[(at + marker.Length)..];
            if (excerpt.Length > 0)
                return line => line.Trim().StartsWith(excerpt, StringComparison.Ordinal);
        }

        var rule = f.ScanCheckRuleId is { } id ? source.ScanCheckRules.FirstOrDefault(r => r.CheckRuleId == id) : null;
        var keyword = rule?.TargetField.Trim('*').Trim();
        return string.IsNullOrEmpty(keyword)
            ? null
            : line => line.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>FileContent: lines holding the extracted identifier — only when it came
    /// from the IdentifierLocator (the fallback SourceId is just the file name).</summary>
    internal static Func<string, bool>? ContentMatcher(JobFailure f)
    {
        var id = f.SourceId;
        if (string.IsNullOrWhiteSpace(id) || id.Length < 2) return null;
        var path = f.SourceFilePath ?? f.SourceLogPath;
        if (string.Equals(id, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)) return null;
        return line => line.Contains(id, StringComparison.Ordinal);
    }

    private static IReadOnlyList<int> MatchLinesIn(string content, Func<string, bool>? matcher)
    {
        if (matcher is null) return [];
        var hits = new List<int>();
        var n = 0;
        foreach (var line in content.Split('\n'))
        {
            n++;
            if (matcher(line.TrimEnd('\r')) && hits.Count < MaxMatchLines) hits.Add(n);
        }
        return hits;
    }

    private static bool LooksLikeXml(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".config", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xsd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Indented XML, or null when the file doesn't parse (shown raw instead).
    /// DTDs prohibited and no resolver — an operator file must not trigger XXE.</summary>
    private static async Task<string?> TryPrettyXmlAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = OpenShared(path);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true };
            using var xr = XmlReader.Create(stream, settings);
            var doc = await XDocument.LoadAsync(xr, LoadOptions.None, ct);
            var body = doc.ToString(SaveOptions.None);
            return doc.Declaration is null ? body : doc.Declaration + Environment.NewLine + body;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    // Live logs are still being written (and may be rolled) — never lock them.
    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal sealed record TextWindow(string Content, int StartLine, IReadOnlyList<int> MatchLines, bool Truncated);

    /// <summary>
    /// The whole file when it fits in <paramref name="maxBytes"/> characters; otherwise a
    /// window of that size centred on the LAST matching line (the newest, for a
    /// tail-following log) — or the end of the file when nothing matches. Two streaming
    /// passes so a multi-GB log never sits in memory. Over-long lines are cut.
    /// </summary>
    internal static async Task<TextWindow> ReadTextWindowAsync(
        string path, int maxBytes, Func<string, bool>? matcher, CancellationToken ct)
    {
        // Pass 1: line count + match line numbers (+ the content when it fits).
        var matches   = new List<int>();
        var all       = new StringBuilder();
        var fits      = true;
        var cut       = false;
        var lineCount = 0;
        await using (var stream = OpenShared(path))
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineCount++;
                if (matcher is not null && matches.Count < MaxMatchLines && matcher(line)) matches.Add(lineCount);
                if (!fits) continue;
                var shown = Cut(line, ref cut);
                if (all.Length + shown.Length + 1 > maxBytes) { fits = false; all.Clear(); continue; }
                all.Append(shown).Append('\n');
            }
        }
        if (fits)
            return new TextWindow(TrimLastNewline(all), 1, matches, cut);

        // Pass 2: lines around the target, half the budget before it, the rest after.
        var target = matches.Count > 0 ? matches[^1] : lineCount;
        var before = new LinkedList<string>();
        var beforeLen = 0;
        var after  = new StringBuilder();
        var n = 0;
        await using (var stream = OpenShared(path))
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                n++;
                var shown = Cut(line, ref cut);
                if (n <= target)
                {
                    before.AddLast(shown);
                    beforeLen += shown.Length + 1;
                    // Keep the target line itself even if it alone exceeds the half budget.
                    var budget = n == target ? maxBytes : maxBytes / 2;
                    while (beforeLen > budget && before.Count > 1)
                    {
                        beforeLen -= before.First!.Value.Length + 1;
                        before.RemoveFirst();
                    }
                }
                else
                {
                    if (beforeLen + after.Length + shown.Length + 1 > maxBytes) break;
                    after.Append(shown).Append('\n');
                }
            }
        }

        var startLine = target - before.Count + 1;
        var content   = new StringBuilder(beforeLen + after.Length);
        foreach (var l in before) content.Append(l).Append('\n');
        content.Append(after);
        var endLine   = target + after.ToString().Count(c => c == '\n');
        var inWindow  = matches.Where(m => m >= startLine && m <= endLine).ToList();
        return new TextWindow(TrimLastNewline(content), startLine, inWindow, Truncated: true);
    }

    private static string Cut(string line, ref bool cut)
    {
        if (line.Length <= MaxLineLength) return line;
        cut = true;
        return line[..MaxLineLength] + " … [line cut]";
    }

    private static string TrimLastNewline(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        return sb.ToString();
    }
}
