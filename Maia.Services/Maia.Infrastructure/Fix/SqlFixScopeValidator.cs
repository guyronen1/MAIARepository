using System.Text.RegularExpressions;
using Maia.Core.Interfaces;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maia.Infrastructure.Fix;

/// <summary>
/// ScriptDom-backed <see cref="ISqlFixScopeValidator"/>. Substitutes
/// <c>{sourceId}</c> or <c>{referenceId}</c> → a sentinel token, parses the script
/// with the official T-SQL parser, and requires the sentinel to appear inside the
/// WHERE clause of every UPDATE/DELETE (or inside an EXEC's arguments). String/regex
/// matching is deliberately avoided — only the AST tells us the placeholder is
/// structurally *in the WHERE* rather than merely present somewhere in the text.
///
/// A WHERE that contains neither sentinel is REJECTED even when a WHERE clause
/// exists — "has a WHERE" is not the same as "scoped by a captured identity."
/// </summary>
public sealed class SqlFixScopeValidator : ISqlFixScopeValidator
{
    private const string Sentinel = "__MAIA_SRC__";

    // Both {sourceId} and {referenceId} (any case) substitute to the same sentinel so
    // either token satisfies the write-guard. {failureId} is intentionally NOT
    // substituted — MAIA's internal PK has no meaning as a source-table key.
    private static readonly Regex ScopeToken =
        new(@"\{sourceId\}|\{referenceId\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string WhereReason =
        "must reference {sourceId} or {referenceId} in the WHERE clause to target the " +
        "specific failure's row. A WHERE clause that references neither captured identity " +
        "is not an acceptable scope guard — bulk UPDATE/DELETE is not allowed.";

    public string? Validate(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "has no SQL.";

        // Strip the optional "ConnectionName|SQL" prefix (mirrors SqlScriptExecutor.SplitPayload).
        var sql = payload;
        var pipe = payload.IndexOf('|');
        if (pipe > 0) sql = payload[(pipe + 1)..];

        // {sourceId} / {referenceId} (any case) → sentinel so the script parses AND
        // either placeholder is locatable in the AST. {failureId} is intentionally
        // NOT substituted — MAIA's internal PK is not an acceptable scope token.
        var substituted = ScopeToken.Replace(sql, Sentinel);

        const string UnparseableReason =
            "could not be parsed as T-SQL — a DbFix must be a single scoped UPDATE/DELETE or EXEC statement.";

        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        TSqlFragment tree;
        IList<ParseError> errors;
        using (var reader = new StringReader(substituted))
            tree = parser.Parse(reader, out errors);

        // Can't verify scoping on something we can't parse — reject (write path).
        if (errors.Count > 0 || tree is not TSqlScript script)
            return UnparseableReason;

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count == 0)
            return UnparseableReason;

        var tokens = tree.ScriptTokenStream;
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case UpdateStatement u:
                    if (!FragmentHasSentinel(u.UpdateSpecification?.WhereClause, tokens)) return WhereReason;
                    break;
                case DeleteStatement d:
                    if (!FragmentHasSentinel(d.DeleteSpecification?.WhereClause, tokens)) return WhereReason;
                    break;
                case ExecuteStatement e:
                    if (!FragmentHasSentinel(e, tokens))
                        return "EXEC must pass {sourceId} or {referenceId} as a parameter to target the specific failure's row.";
                    break;
                default:
                    return $"must be a scoped UPDATE, DELETE, or EXEC statement (got {Describe(stmt)}).";
            }
        }

        return null;
    }

    /// <summary>True when any token spanning <paramref name="frag"/> contains the
    /// sentinel — i.e. the (substituted) {sourceId} sits inside this fragment.</summary>
    private static bool FragmentHasSentinel(TSqlFragment? frag, IList<TSqlParserToken> tokens)
    {
        if (frag is null || frag.FirstTokenIndex < 0 || frag.LastTokenIndex < 0) return false;
        for (var i = frag.FirstTokenIndex; i <= frag.LastTokenIndex && i < tokens.Count; i++)
            if (tokens[i].Text?.IndexOf(Sentinel, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static string Describe(TSqlStatement stmt)
    {
        var n = stmt.GetType().Name;
        return n.EndsWith("Statement", StringComparison.Ordinal) ? n[..^"Statement".Length] : n;
    }
}
