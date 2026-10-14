using System.Text.RegularExpressions;
using Maia.Core.Enums;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// The engine-specific pieces of the SQL that <see cref="DatabaseScanStrategy"/>
/// generates for table rules (ColumnRange / ValueEquals): identifier quoting, bind
/// placeholders, value-to-text projections and the row-capped query shape. Picked per
/// scan source from its connection's <see cref="DbProvider"/>. The SQL Server dialect
/// reproduces the pre-Oracle T-SQL byte-for-byte; SqlQuery rules don't go through here
/// (the operator writes that SQL in the engine's own syntax).
/// </summary>
internal abstract class SqlDialect
{
    public static readonly SqlDialect SqlServer = new SqlServerDialect();
    public static readonly SqlDialect Oracle    = new OracleDialect();

    public static SqlDialect For(DbProvider provider) => provider == DbProvider.Oracle ? Oracle : SqlServer;

    /// <summary>Quotes one identifier part (no dots).</summary>
    public abstract string QuoteIdentifier(string identifier);

    /// <summary>"dbo.Files" → each dot-separated part quoted.</summary>
    public string QuoteTable(string sourceTable)
        => string.Join(".", sourceTable.Split('.').Select(QuoteTablePart));

    protected virtual string QuoteTablePart(string part) => QuoteIdentifier(part);

    /// <summary>Column with an optional "alias.Column" prefix (JOIN in SourceTable).</summary>
    public abstract string QuoteColumnRef(string columnRef);

    /// <summary>The name the DbParameter is registered under for logical name <paramref name="name"/>.</summary>
    public abstract string ParamName(string name);

    /// <summary>The placeholder that references <paramref name="name"/> inside the SQL text.</summary>
    public abstract string Placeholder(string name);

    /// <summary>Result-set alias for a MAIA-internal projected column (e.g. _WatermarkVal).</summary>
    public abstract string Alias(string name);

    /// <summary>Watermark column as sortable text (ISO for dates).</summary>
    public abstract string WatermarkText(string expr);

    /// <summary>Identity-type column as text, truncated to <paramref name="maxLength"/>.</summary>
    public abstract string IdText(string expr, int maxLength);

    /// <summary>Detail column projection; may be raw — the reader canonicalises non-strings.</summary>
    public abstract string DetailText(string expr);

    /// <summary>Condition column as text (ISO for dates) for the message token.</summary>
    public abstract string ConditionText(string expr);

    /// <summary>
    /// The capped matching-rows query. Column 0 = row key text, column 1 = the target
    /// field, then <paramref name="extraColumns"/> (each already starting with ", ").
    /// With a watermark column the Oracle shape returns rows ordered by it ascending.
    /// </summary>
    public abstract string MatchingRowsQuery(string targetColumn, string extraColumns, string table,
        string where, string? orderByColumn, int maxRows);

    /// <summary>MAX(watermark) as text over the rule-filtered rows.</summary>
    public abstract string FilteredMaxQuery(string watermarkColumn, string table, string where);

    private sealed class SqlServerDialect : SqlDialect
    {
        public override string QuoteIdentifier(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
        protected override string QuoteTablePart(string part) => QuoteIdentifier(part.Trim('[', ']'));

        public override string QuoteColumnRef(string columnRef)
        {
            var dot = columnRef.LastIndexOf('.');
            return dot < 0
                ? QuoteIdentifier(columnRef)
                : $"{columnRef[..dot]}.{QuoteIdentifier(columnRef[(dot + 1)..])}";
        }

        public override string ParamName(string name)   => "@" + name;
        public override string Placeholder(string name) => "@" + name;
        public override string Alias(string name)       => name;

        // Style 121 = ISO `yyyy-mm-dd hh:mi:ss.fffffff`; ignored for non-date types.
        public override string WatermarkText(string expr)            => $"CONVERT(NVARCHAR(50), {expr}, 121)";
        public override string IdText(string expr, int maxLength)    => $"CAST({expr} AS NVARCHAR({maxLength}))";
        public override string DetailText(string expr)               => $"CONVERT(NVARCHAR(MAX), {expr}, 121)";
        public override string ConditionText(string expr)            => $"CONVERT(NVARCHAR(MAX), {expr}, 121)";

        public override string MatchingRowsQuery(string targetColumn, string extraColumns, string table,
            string where, string? orderByColumn, int maxRows)
        {
            var orderBy = orderByColumn is not null
                ? $"ORDER BY {orderByColumn} ASC"
                : "ORDER BY (SELECT NULL)";
            return $"""
                SELECT TOP {maxRows}
                    CAST(ROW_NUMBER() OVER ({orderBy}) AS NVARCHAR(20)) AS _RowKey,
                    {targetColumn}
                    {extraColumns}
                FROM {table}
                WHERE {where}
                """;
        }

        public override string FilteredMaxQuery(string watermarkColumn, string table, string where)
            => $"SELECT CONVERT(NVARCHAR(50), MAX({watermarkColumn}), 121) FROM {table} WHERE {where}";
    }

    /// <summary>
    /// Oracle SQL. Relies on the session formats <see cref="DbConnections.OpenAsync(DbProvider, string, CancellationToken)"/>
    /// pins (ISO NLS date/timestamp formats, '.' decimal point) so <c>TO_CHAR(col)</c> is
    /// type-agnostic and a stored watermark string binds back against DATE / TIMESTAMP /
    /// NUMBER columns. Uses <c>ROWNUM</c> rather than <c>FETCH FIRST</c> so it runs on
    /// 11g as well as 12c+.
    /// </summary>
    private sealed class OracleDialect : SqlDialect
    {
        // An identifier Oracle would accept unquoted. Unquoted names are stored upper-case,
        // so "Status" means STATUS — quoting it as-is ("Status") would look for a
        // mixed-case column that doesn't exist. Upper-casing + quoting keeps the operator's
        // natural casing working AND still lets reserved-word column names through.
        private static readonly Regex Plain = new(@"^[A-Za-z][A-Za-z0-9_$#]*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Plain names → upper-cased and double-quoted (same meaning as unquoted).
        /// Operator-quoted names ("MixedCase") → exact, case-sensitive. Anything else
        /// (spaces etc.) → quoted exactly. Oracle identifiers can never contain a
        /// double quote, so one left inside is refused rather than escaped.
        /// </summary>
        public override string QuoteIdentifier(string identifier)
        {
            var s = identifier.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                s = s[1..^1];
            else
            {
                s = s.Trim('[', ']');
                if (Plain.IsMatch(s)) s = s.ToUpperInvariant();
            }
            if (s.Length == 0 || s.Contains('"'))
                throw new InvalidOperationException($"'{identifier}' is not a valid Oracle identifier.");
            return "\"" + s + "\"";
        }

        public override string QuoteColumnRef(string columnRef)
        {
            var dot = columnRef.LastIndexOf('.');
            return dot < 0
                ? QuoteIdentifier(columnRef)
                : $"{QuoteTable(columnRef[..dot])}.{QuoteIdentifier(columnRef[(dot + 1)..])}";
        }

        // "p_" prefix: a bare bind name that happens to be an Oracle reserved word
        // (e.g. :Date, :Level) fails with ORA-01745.
        public override string ParamName(string name)   => "p_" + name;
        public override string Placeholder(string name) => ":p_" + name;
        // Leading underscore is only legal quoted.
        public override string Alias(string name)       => "\"" + name + "\"";

        public override string WatermarkText(string expr)         => $"TO_CHAR({expr})";
        public override string IdText(string expr, int maxLength) => $"SUBSTR(TO_CHAR({expr}), 1, {maxLength})";
        // Raw: TO_CHAR on a CLOB over 4000 bytes raises ORA-22835, and detail columns are
        // exactly where CLOBs live. The reader canonicalises dates / numbers.
        public override string DetailText(string expr)            => expr;
        public override string ConditionText(string expr)         => $"TO_CHAR({expr})";

        public override string MatchingRowsQuery(string targetColumn, string extraColumns, string table,
            string where, string? orderByColumn, int maxRows)
        {
            // ROWNUM is assigned after the inner ORDER BY, so with a watermark the cap
            // keeps the OLDEST rows and the last non-null watermark in the result is the
            // true maximum by the column's native type.
            var orderBy = orderByColumn is not null ? $"\n        ORDER BY {orderByColumn} ASC" : string.Empty;
            return $"""
                SELECT TO_CHAR(ROWNUM) AS "_RowKey", q.*
                FROM (
                    SELECT {targetColumn}
                        {extraColumns}
                    FROM {table}
                    WHERE {where}{orderBy}
                ) q
                WHERE ROWNUM <= {maxRows}
                """;
        }

        public override string FilteredMaxQuery(string watermarkColumn, string table, string where)
            => $"SELECT TO_CHAR(MAX({watermarkColumn})) FROM {table} WHERE {where}";
    }
}
