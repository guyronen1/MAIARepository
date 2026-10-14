using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Results;
using Maia.Infrastructure.Scanning;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// "View Source" reader — the DB-free halves: rule resolution, lookup SQL per dialect,
/// SqlQuery key filtering, value rendering, and the file path (path guard, matched-line
/// detection, the window over a big log, XML pretty-print).
/// </summary>
public sealed class SourceDataReaderTests : IDisposable
{
    private readonly string _dir;

    public SourceDataReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maia-srcview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ScanCheckRule DbRule(int id, string table, string? sourceIdColumn = null,
        CheckType type = CheckType.ValueEquals, string? description = null) => new()
    {
        CheckRuleId = id, CheckType = type, SourceTable = table, TargetField = "Status",
        SourceIdColumn = sourceIdColumn, Description = description,
    };

    private JobFailure Failure(string path, string? message = null, string? stepName = null,
        string? sourceId = null, string? sourceFilePath = null, int? ruleId = null) => new()
    {
        FailureId = 1, SourceLogPath = path, ErrorMessage = message, StepName = stepName ?? Path.GetFileName(path),
        SourceId = sourceId, SourceFilePath = sourceFilePath, ScanCheckRuleId = ruleId,
    };

    private ScanSource Source(params ScanCheckRule[] rules) => new()
    {
        Name = "src", LogFolder = _dir, ScanCheckRules = rules.ToList(),
    };

    // ── Rule resolution ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveDbRule_prefers_the_stored_rule_id()
    {
        var a = DbRule(1, "dbo.Files", "FileId");
        var b = DbRule(2, "dbo.Files", "FileId");
        var f = new JobFailure { SourceLogPath = "db://x/dbo.Files", StepName = "dbo.Files", ScanCheckRuleId = 2 };
        Assert.Same(b, SourceDataReader.ResolveDbRule(f, [a, b]));
    }

    [Fact]
    public void ResolveDbRule_legacy_row_matches_table_by_step_name_preferring_a_key_column()
    {
        var noKey  = DbRule(1, "dbo.Files");
        var keyed  = DbRule(2, "dbo.Files", "FileId");
        var other  = DbRule(3, "dbo.Other", "Id");
        var f = new JobFailure { SourceLogPath = "db://x/dbo.Files", StepName = "dbo.Files" };
        Assert.Same(keyed, SourceDataReader.ResolveDbRule(f, [noKey, keyed, other]));
    }

    [Fact]
    public void ResolveDbRule_deleted_rule_falls_back_to_step_name()
    {
        var current = DbRule(7, "dbo.Files", "FileId");
        var f = new JobFailure { SourceLogPath = "db://x/dbo.Files", StepName = "dbo.Files", ScanCheckRuleId = 99 };
        Assert.Same(current, SourceDataReader.ResolveDbRule(f, [current]));
    }

    [Theory]
    [InlineData("Failed imports", "Failed imports")]   // Description is the step name
    [InlineData(null, "SqlQuery #5")]                  // blank Description → per-rule label
    public void ResolveDbRule_matches_SqlQuery_label(string? description, string stepName)
    {
        var q = DbRule(5, "SELECT * FROM x", "Id", CheckType.SqlQuery, description);
        var f = new JobFailure { SourceLogPath = "db://x/query", StepName = stepName };
        Assert.Same(q, SourceDataReader.ResolveDbRule(f, [q]));
    }

    [Fact]
    public void ResolveDbRule_returns_null_when_nothing_matches()
    {
        var f = new JobFailure { SourceLogPath = "db://x/dbo.Gone", StepName = "dbo.Gone" };
        Assert.Null(SourceDataReader.ResolveDbRule(f, [DbRule(1, "dbo.Files", "FileId")]));
    }

    [Fact]
    public void HighlightColumns_cover_target_conditions_and_key_without_alias()
    {
        var rule = DbRule(1, "dbo.Files", "f.FileId");
        rule.Conditions.Add(new ScanCheckRuleCondition { ConditionOrder = 1, Kind = CheckType.ValueEquals, TargetField = "Region" });
        rule.Conditions.Add(new ScanCheckRuleCondition { ConditionOrder = 2, Kind = CheckType.ValueEquals, TargetField = "status" });
        Assert.Equal(new[] { "Status", "Region", "FileId" }, SourceDataReader.HighlightColumnsOf(rule));
    }

    // ── Lookup SQL ────────────────────────────────────────────────────────────

    [Fact]
    public void KeyLookupSql_SqlServer_native_and_text()
    {
        Assert.Equal("SELECT TOP 5 * FROM [dbo].[Files] WHERE [FileId] = @Id",
            SourceDataReader.KeyLookupSql(SqlDialect.SqlServer, "dbo.Files", "FileId", asText: false));
        Assert.Equal("SELECT TOP 5 * FROM [dbo].[Files] WHERE CAST([FileId] AS NVARCHAR(100)) = @Id",
            SourceDataReader.KeyLookupSql(SqlDialect.SqlServer, "dbo.Files", "FileId", asText: true));
    }

    [Fact]
    public void KeyLookupSql_Oracle_native_and_text()
    {
        Assert.Equal("""SELECT * FROM "APP"."FILES" WHERE ("FILE_ID" = :p_Id) AND ROWNUM <= 5""",
            SourceDataReader.KeyLookupSql(SqlDialect.Oracle, "app.files", "file_id", asText: false));
        Assert.Equal("""SELECT * FROM "APP"."FILES" WHERE (SUBSTR(TO_CHAR("FILE_ID"), 1, 100) = :p_Id) AND ROWNUM <= 5""",
            SourceDataReader.KeyLookupSql(SqlDialect.Oracle, "app.files", "file_id", asText: true));
    }

    // ── SqlQuery filtering + value rendering ──────────────────────────────────

    [Fact]
    public void FilterByKey_keeps_matching_rows_in_column_order()
    {
        IReadOnlyDictionary<string, object?> Row(int id, string status) =>
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Id"] = id, ["Status"] = status, ["Note"] = null };

        var rows = SourceDataReader.FilterByKey([Row(1, "ok"), Row(42, "bad"), Row(3, "ok")], "id", "42");

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "Id", "Status", "Note" }, row.Select(c => c.Name));
        Assert.Equal("42", row[0].Value);
        Assert.Null(row[2].Value);   // SQL NULL stays null (rendered as NULL by the client)
    }

    [Fact]
    public void Column_renders_dates_iso_bytes_hex_and_cuts_long_text()
    {
        Assert.Equal("2026-10-14 09:05:03.120", SourceDataReader.Column("d", new DateTime(2026, 10, 14, 9, 5, 3, 120)).Value);
        Assert.Equal("2026-10-14", SourceDataReader.Column("d", new DateTime(2026, 10, 14)).Value);
        Assert.Equal("0x0AFF", SourceDataReader.Column("b", new byte[] { 0x0A, 0xFF }).Value);
        Assert.Equal("1.5", SourceDataReader.Column("n", 1.5m).Value);

        var longCol = SourceDataReader.Column("t", new string('x', SourceDataReader.MaxValueLength + 10));
        Assert.True(longCol.Truncated);
        Assert.Equal(SourceDataReader.MaxValueLength, longCol.Value!.Length);
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Log_file_is_returned_whole_with_the_matched_line()
    {
        var path = WriteFile("app.log", "start\nall good\n  ERROR disk full on D:  \nend\n");
        var f = Failure(path, message: "[ERROR] app.log: ERROR disk full on D:");

        var r = await SourceDataReader.ReadFileAsync(f, Source(), ScanType.FileSystem, 1_000_000, default);

        Assert.Equal(SourceDataKind.File, r.Kind);
        Assert.Equal("text", r.Format);
        Assert.False(r.Truncated);
        Assert.Equal(1, r.WindowStartLine);
        Assert.Equal(new[] { 3 }, r.MatchLines);
        Assert.Equal("start\nall good\n  ERROR disk full on D:  \nend", r.Content);
    }

    [Fact]
    public async Task Log_match_falls_back_to_rule_keyword_when_message_has_another_shape()
    {
        var path = WriteFile("app.log", "a\nTimeout here\nb\ntimeout again\n");
        var rule = new ScanCheckRule { CheckRuleId = 4, CheckType = CheckType.ErrorKeyword, TargetField = "*Timeout*" };
        var f = Failure(path, message: "something else", ruleId: 4);

        var r = await SourceDataReader.ReadFileAsync(f, Source(rule), ScanType.FileSystem, 1_000_000, default);

        Assert.Equal(new[] { 2, 4 }, r.MatchLines);
    }

    [Fact]
    public async Task Big_log_returns_a_window_centred_on_the_last_match()
    {
        var lines = Enumerable.Range(1, 5000).Select(i => i == 4000 ? "line 4000 FATAL boom" : $"line {i:D4} fine").ToList();
        var path = WriteFile("big.log", string.Join("\n", lines) + "\n");
        var f = Failure(path, message: "[FATAL] big.log: line 4000 FATAL boom");

        var r = await SourceDataReader.ReadFileAsync(f, Source(), ScanType.FileSystem, maxBytes: 2000, default);

        Assert.True(r.Truncated);
        Assert.Equal(new[] { 4000 }, r.MatchLines);
        var shown = r.Content!.Split('\n');
        Assert.True(r.Content!.Length <= 2000);
        Assert.Equal($"line {r.WindowStartLine:D4} fine", shown[0]);
        var idx = 4000 - r.WindowStartLine;
        Assert.Equal("line 4000 FATAL boom", shown[idx]);
        Assert.InRange(idx, shown.Length / 3, shown.Length * 2 / 3);   // roughly centred
    }

    [Fact]
    public async Task Big_log_without_a_match_shows_the_end()
    {
        var path = WriteFile("big.log", string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"line {i:D4}")) + "\n");
        var f = Failure(path, message: "[X] big.log: not in the file");

        var r = await SourceDataReader.ReadFileAsync(f, Source(), ScanType.FileSystem, maxBytes: 1000, default);

        Assert.True(r.Truncated);
        Assert.Empty(r.MatchLines);
        Assert.EndsWith("line 3000", r.Content);
    }

    [Fact]
    public async Task File_outside_the_source_folder_is_refused()
    {
        // Sibling-prefix trap: "<dir>-evil" must not count as inside "<dir>".
        var sibling = _dir + "-evil";
        Directory.CreateDirectory(sibling);
        var outside = Path.Combine(sibling, "secret.log");
        File.WriteAllText(outside, "secret");
        try
        {
            var r = await SourceDataReader.ReadFileAsync(Failure(outside), Source(), ScanType.FileSystem, 1_000_000, default);
            Assert.Equal(SourceDataKind.Unavailable, r.Kind);
            Assert.Null(r.Content);

            // "..\" traversal out of the folder is normalised away first.
            var traversal = Path.Combine(_dir, "..", Path.GetFileName(sibling), "secret.log");
            r = await SourceDataReader.ReadFileAsync(Failure(traversal), Source(), ScanType.FileSystem, 1_000_000, default);
            Assert.Equal(SourceDataKind.Unavailable, r.Kind);
        }
        finally { Directory.Delete(sibling, recursive: true); }
    }

    [Fact]
    public async Task Missing_file_is_unavailable()
    {
        var r = await SourceDataReader.ReadFileAsync(Failure(Path.Combine(_dir, "rolled.log")), Source(), ScanType.FileSystem, 1_000_000, default);
        Assert.Equal(SourceDataKind.Unavailable, r.Kind);
        Assert.Contains("no longer exists", r.Reason);
    }

    [Fact]
    public async Task Xml_content_file_is_pretty_printed_and_identifier_highlighted()
    {
        var path = WriteFile("order.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Order><Id>ORD-77</Id><Status>Rejected</Status></Order>");
        var f = Failure(path, sourceId: "ORD-77", sourceFilePath: path);

        var r = await SourceDataReader.ReadFileAsync(f, Source(), ScanType.FileContent, 1_000_000, default);

        Assert.Equal("xml", r.Format);
        var shown = r.Content!.Replace("\r\n", "\n").Split('\n');
        Assert.StartsWith("<?xml", shown[0]);
        Assert.Equal("<Order>", shown[1]);
        Assert.Equal(new[] { 3 }, r.MatchLines);
        Assert.Contains("ORD-77", shown[2]);
    }

    [Fact]
    public async Task Malformed_xml_is_shown_raw()
    {
        var path = WriteFile("broken.xml", "<Order><Id>1</Id>");
        var r = await SourceDataReader.ReadFileAsync(Failure(path, sourceFilePath: path), Source(), ScanType.FileContent, 1_000_000, default);
        Assert.Equal("text", r.Format);
        Assert.Equal("<Order><Id>1</Id>", r.Content);
    }

    [Fact]
    public async Task Xml_with_a_dtd_is_not_parsed()
    {
        var path = WriteFile("dtd.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e;</x>");
        var r = await SourceDataReader.ReadFileAsync(Failure(path, sourceFilePath: path), Source(), ScanType.FileContent, 1_000_000, default);
        Assert.Equal("text", r.Format);   // raw text, entity never expanded
        Assert.Contains("&e;", r.Content);
    }
}
