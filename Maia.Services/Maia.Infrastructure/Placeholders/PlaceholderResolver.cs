using System.IO;
using System.Text.RegularExpressions;
using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.Placeholders;

/// <summary>
/// Single source of truth for payload placeholder substitution. Loads the
/// failure + MonitoredJob navigation once per call so composite steps
/// resolving the same template repeatedly stay cheap.
///
/// Token grammar (intentionally narrow): {name} where name is [A-Za-z][A-Za-z0-9]*.
/// Matching is case-insensitive; unknown names are left literal so downstream
/// tools / log greps can still spot them.
/// </summary>
public sealed class PlaceholderResolver(IDbContextFactory<MaiaDbContext> factory)
    : IPlaceholderResolver
{
    private static readonly Regex Token = new(
        @"\{(?<name>[a-zA-Z][a-zA-Z0-9]*)\}",
        RegexOptions.Compiled);

    // Same token for the SQL path, but the FIRST alternative also consumes a
    // MATCHED PAIR of surrounding single quotes: 'sourceId' → @pN so the
    // operator's WHERE Id = '{sourceId}' becomes WHERE Id = @p0, not '@p0'.
    // A lone quote on one side is deliberately NOT matched by the pair branch —
    // it falls through to the bare branch, leaving the quotes intact (the token
    // becomes a harmless literal fragment, e.g. LIKE '@p0%', never an injection
    // and never an unbalanced-quote syntax error).
    private static readonly Regex SqlToken = new(
        @"'\{(?<name>[a-zA-Z][a-zA-Z0-9]*)\}'|\{(?<name>[a-zA-Z][a-zA-Z0-9]*)\}",
        RegexOptions.Compiled);

    public async Task<string> ResolveAsync(
        string template,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var failure = await LoadFailureAsync(recommendation.FailureId, ct);
        return Substitute(template, recommendation, failure, requiredPlaceholders: null);
    }

    public async Task<string> ResolveOrThrowAsync(
        string template,
        AiRecommendation recommendation,
        IReadOnlyCollection<string> requiredPlaceholders,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var failure = await LoadFailureAsync(recommendation.FailureId, ct);
        return Substitute(template, recommendation, failure, requiredPlaceholders);
    }

    public async Task<ParameterizedSql> ResolveSqlAsync(
        string template,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(template))
            return new ParameterizedSql(template, Array.Empty<SqlPlaceholderParameter>());

        var failure    = await LoadFailureAsync(recommendation.FailureId, ct);
        var parameters = new List<SqlPlaceholderParameter>();

        var sql = SqlToken.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            if (!TryResolveValue(name, recommendation, failure, out var value))
                return m.Value;   // unknown → left literal (surrounding quotes preserved)

            var paramName = "@p" + parameters.Count;
            parameters.Add(new SqlPlaceholderParameter(paramName, value));
            return paramName;
        });

        return new ParameterizedSql(sql, parameters);
    }

    private async Task<JobFailure?> LoadFailureAsync(int failureId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures
            .Include(j => j.MonitoredJob)
            .Include(j => j.ScanSource)
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.FailureId == failureId, ct);
    }

    private static string Substitute(
        string template,
        AiRecommendation rec,
        JobFailure? failure,
        IReadOnlyCollection<string>? requiredPlaceholders)
    {
        // Build a case-insensitive required-set once for membership checks.
        var required = requiredPlaceholders is null
            ? null
            : new HashSet<string>(requiredPlaceholders, StringComparer.OrdinalIgnoreCase);

        return Token.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            if (!TryResolveValue(name, rec, failure, out var value))
                return m.Value;   // unknown → left literal

            if (required is not null
                && required.Contains(name)
                && string.IsNullOrEmpty(value))
            {
                throw new PlaceholderUnresolvedException(BuildSpecificError(name, rec, failure))
                {
                    PlaceholderName = name
                };
            }
            return value;
        });
    }

    /// <summary>
    /// The single token→value table, shared by the text (<see cref="Substitute"/>)
    /// and SQL (<see cref="ResolveSqlAsync"/>) paths so a new placeholder is added
    /// in exactly one place. Returns false for an unrecognised name (caller leaves
    /// it literal); recognised-but-unavailable values resolve to empty string.
    /// </summary>
    private static bool TryResolveValue(
        string name, AiRecommendation rec, JobFailure? failure, out string value)
    {
        switch (name.ToLowerInvariant())
        {
            case "failureid":      value = rec.FailureId.ToString();            return true;
            case "sourceid":       value = failure?.SourceId       ?? string.Empty; return true;
            case "referenceid":    value = failure?.ReferenceId    ?? string.Empty; return true;
            case "sourcelogpath":  value = failure?.SourceLogPath  ?? string.Empty; return true;
            case "sourcefilepath": value = failure?.SourceFilePath ?? string.Empty; return true;
            // Filename-only slice of {sourceFilePath} (handles both \ and /
            // separators). Empty when no source path was captured. Lets a
            // CopyFile dest reuse the original name: {inputFolder}\{sourceFileName}.
            case "sourcefilename":
                value = failure?.SourceFilePath is { Length: > 0 } sfp
                    ? Path.GetFileName(sfp) : string.Empty;
                return true;
            case "jobfolder":      value = failure?.ScanSource?.LogFolder   ?? string.Empty; return true;
            case "inputfolder":    value = failure?.ScanSource?.InputFolder ?? string.Empty; return true;
            default:               value = string.Empty; return false;
        }
    }

    private static string BuildSpecificError(string name, AiRecommendation rec, JobFailure? f)
    {
        // {sourceFilePath} gets the actionable triage path — it's the placeholder
        // operators are most likely to mis-configure (forgetting to set
        // InputPathPattern on FS rules or FilePathColumn on DB rules).
        if (string.Equals(name, "sourceFilePath", StringComparison.OrdinalIgnoreCase))
        {
            var jobName = f?.MonitoredJob?.Name ?? "?";
            return $"Step uses {{sourceFilePath}} but failure {rec.FailureId} " +
                   $"(job '{jobName}') has no source path captured — configure " +
                   $"InputPathPattern on the FS scan rule, or FilePathColumn on " +
                   $"the DB scan rule, for this job.";
        }
        return $"Step uses {{{name}}} but the value is empty for failure {rec.FailureId}.";
    }
}
