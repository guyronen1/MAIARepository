using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

/// <summary>
/// Resolves payload placeholders against a recommendation's failure context.
/// Centralised so adding a new placeholder is one edit, not five — every
/// executor (SqlScript, Script, CopyFile, Composite steps) calls this.
///
/// Supported placeholders (case-insensitive):
///   {failureId}      — JobFailure.FailureId (int)
///   {sourceId}       — JobFailure.SourceId (string; row's natural key)
///   {sourceLogPath}  — JobFailure.SourceLogPath (where the error was DETECTED)
///   {sourceFilePath} — JobFailure.SourceFilePath (the INPUT file being worked on; nullable)
///   {sourceFileName} — filename-only slice of {sourceFilePath} (Path.GetFileName; empty if no path)
///   {jobFolder}      — ScanSource.LogFolder (FS / FileContent scan sources)
///   {inputFolder}    — ScanSource.InputFolder (FS scan sources; rare)
///
/// Unknown placeholders are left literal so downstream tooling can spot them.
/// </summary>
public interface IPlaceholderResolver
{
    /// <summary>
    /// Substitute every recognised {placeholder} in <paramref name="template"/>.
    /// Null/empty values resolve to empty string. Use
    /// <see cref="ResolveOrThrowAsync"/> when a specific placeholder must
    /// have a non-empty value.
    /// </summary>
    Task<string> ResolveAsync(
        string template,
        AiRecommendation recommendation,
        CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="ResolveAsync"/> but throws
    /// <see cref="PlaceholderUnresolvedException"/> when any of
    /// <paramref name="requiredPlaceholders"/> is used in the template AND
    /// resolves to null/empty for this failure. Use when a step
    /// fundamentally cannot proceed without the value (e.g. CopyFile needs
    /// a non-empty source path).
    /// </summary>
    Task<string> ResolveOrThrowAsync(
        string template,
        AiRecommendation recommendation,
        IReadOnlyCollection<string> requiredPlaceholders,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve a template destined for a SQL command WITHOUT interpolating any
    /// value into the SQL text. Every recognised <c>{placeholder}</c> is replaced
    /// with a positional <c>@pN</c> parameter marker and its value bound in the
    /// returned <see cref="ParameterizedSql.Parameters"/> list — so scanned data
    /// (SourceId, ReferenceId, file paths, …) can never become executable SQL.
    /// This is the SQL-injection-safe counterpart to <see cref="ResolveAsync"/>;
    /// SqlScript fixes (single-action and composite steps) MUST use it.
    ///
    /// Quote handling: operators quote the placeholder (<c>WHERE Id = '{sourceId}'</c>)
    /// and the "scope to the failing row" UI helper appends the same. A placeholder
    /// wrapped in a matched pair of single quotes has those quotes consumed, so the
    /// result is a bare parameter reference (<c>WHERE Id = @p0</c>), never the literal
    /// <c>'@p0'</c>. Unknown placeholders are left literal (quotes preserved).
    /// </summary>
    Task<ParameterizedSql> ResolveSqlAsync(
        string template,
        AiRecommendation recommendation,
        CancellationToken ct = default);
}

/// <summary>
/// Result of <see cref="IPlaceholderResolver.ResolveSqlAsync"/>: the rewritten SQL
/// with <c>@pN</c> markers, plus the parameter values to bind. Provider-neutral so
/// this Core type stays free of any SqlClient dependency — the executor turns each
/// <see cref="SqlPlaceholderParameter"/> into a driver parameter.
/// </summary>
public sealed record ParameterizedSql(
    string Sql,
    IReadOnlyList<SqlPlaceholderParameter> Parameters);

/// <summary>A single bound value: the <c>@pN</c> marker name and its (never-null,
/// empty-when-unresolved) string value.</summary>
public sealed record SqlPlaceholderParameter(string Name, string Value);

public sealed class PlaceholderUnresolvedException : Exception
{
    public required string PlaceholderName { get; init; }
    public PlaceholderUnresolvedException(string message) : base(message) { }
}
