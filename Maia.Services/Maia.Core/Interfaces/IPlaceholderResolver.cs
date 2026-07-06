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
}

public sealed class PlaceholderUnresolvedException : Exception
{
    public required string PlaceholderName { get; init; }
    public PlaceholderUnresolvedException(string message) : base(message) { }
}
