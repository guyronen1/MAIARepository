using Maia.Core.Enums;

namespace Maia.Core.Interfaces;

/// <summary>
/// Pulls a single value out of an input data file by a format-specific locator.
/// One implementation per <see cref="FileFormat"/> (XML in v1), registered as
/// <c>IEnumerable&lt;IFileContentExtractor&gt;</c> and dispatched by Format —
/// same plug-in shape as IFixActionExecutor and IUnconfiguredClusterAnalyzer.
/// </summary>
public interface IFileContentExtractor
{
    /// <summary>The file format this extractor understands.</summary>
    FileFormat Format { get; }

    /// <summary>
    /// Extract the value addressed by <paramref name="locator"/> from the file.
    /// The locator's grammar is the extractor's own (XPath for XML, JsonPath for
    /// JSON, …) — callers treat it as an opaque string.
    /// <para>
    /// Returns <c>null</c> when the locator matches nothing, the locator is
    /// malformed, or the file can't be parsed (e.g. malformed XML) — all "no
    /// value" outcomes the implementation logs at Warning. A non-null result is
    /// trimmed of surrounding whitespace.
    /// </para>
    /// <para>
    /// Throws <see cref="FileContentTooLargeException"/> when the file exceeds
    /// the extractor's size cap — the caller distinguishes this from a null
    /// return and counts it as an oversize skip rather than an extraction miss.
    /// </para>
    /// </summary>
    Task<string?> ExtractAsync(string filePath, string locator, CancellationToken ct = default);

    /// <summary>
    /// Validate a locator's <em>syntax</em> (not against any file) for config
    /// save-time checks. Returns <c>null</c> when the locator is valid (or empty
    /// — empty means "no locator", which is allowed), else a short human-readable
    /// reason. The extractor owns its locator grammar, so it owns this check —
    /// e.g. the XML extractor compiles the string as XPath. Lets ConfigController
    /// reject a malformed locator (a `\\`-vs-`//` typo) at save instead of having
    /// it fail silently at scan time.
    /// </summary>
    string? ValidateLocator(string locator);
}

/// <summary>
/// Thrown by an <see cref="IFileContentExtractor"/> when a file exceeds the
/// hard size cap, before any parse is attempted. The FileContent scan strategy
/// catches this, logs a Warning, increments its oversize-skip counter, and
/// moves on — distinct from an extraction that simply found no value.
/// </summary>
public sealed class FileContentTooLargeException(string filePath, long sizeBytes, long capBytes)
    : Exception($"File '{filePath}' is {sizeBytes:N0} bytes, exceeding the {capBytes:N0}-byte extraction cap.")
{
    public string FilePath  { get; } = filePath;
    public long   SizeBytes { get; } = sizeBytes;
    public long   CapBytes  { get; } = capBytes;
}
