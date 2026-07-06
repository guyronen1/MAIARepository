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
            var name  = m.Groups["name"].Value;
            var value = name.ToLowerInvariant() switch
            {
                "failureid"      => rec.FailureId.ToString(),
                "sourceid"       => failure?.SourceId               ?? string.Empty,
                "referenceid"    => failure?.ReferenceId            ?? string.Empty,
                "sourcelogpath"  => failure?.SourceLogPath           ?? string.Empty,
                "sourcefilepath" => failure?.SourceFilePath          ?? string.Empty,
                // Filename-only slice of {sourceFilePath} (handles both \ and /
                // separators). Empty when no source path was captured. Lets a
                // CopyFile dest reuse the original name: {inputFolder}\{sourceFileName}.
                "sourcefilename" => failure?.SourceFilePath is { Length: > 0 } sfp
                    ? Path.GetFileName(sfp) : string.Empty,
                "jobfolder"      => failure?.ScanSource?.LogFolder   ?? string.Empty,
                "inputfolder"    => failure?.ScanSource?.InputFolder ?? string.Empty,
                _                => m.Value   // unknown → left literal
            };

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
