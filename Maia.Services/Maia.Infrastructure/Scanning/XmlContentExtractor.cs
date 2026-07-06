using System.Collections;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// XPath-based <see cref="IFileContentExtractor"/> for XML input files.
/// Loads the whole document with <see cref="XDocument"/> (fine for the
/// v1 target of 10KB–1MB files), evaluates the locator as an XPath, and returns
/// the first matching value. Supports element, attribute, text/CDATA nodes, and
/// XPath functions (string(), concat(), count(), boolean()).
///
/// Failure handling (all return null, logged at Warning):
///   • locator empty / malformed XPath
///   • XPath matched nothing
///   • file malformed / unreadable
/// Oversize files throw <see cref="FileContentTooLargeException"/> before any
/// parse so the caller can count them distinctly from extraction misses.
///
/// No XPath timeout: unlike Regex, XPath evaluation isn't cancellable mid-run,
/// and on a document already bounded to ≤5MB a reasonable expression completes
/// in microseconds. The size cap is the real defensive bound (see investigation
/// deviation #9).
/// </summary>
public sealed class XmlContentExtractor(ILogger<XmlContentExtractor> logger) : IFileContentExtractor
{
    /// <summary>Hard cap on file size before extraction is attempted. 5MB = 5×
    /// headroom over the largest realistic input (1MB). Checked via FileInfo so
    /// an oversize file is never read into memory.</summary>
    public const long MaxFileSizeBytes = 5L * 1024 * 1024;

    public FileFormat Format => FileFormat.Xml;

    /// <summary>Compile the locator as XPath; null = valid, else the reason.
    /// Empty is valid ("no locator"). Catches the `\\`-vs-`//` typo at save.</summary>
    public string? ValidateLocator(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return null;
        try
        {
            XPathExpression.Compile(locator);
            return null;
        }
        catch (Exception ex) when (ex is XPathException or ArgumentException)
        {
            return $"not valid XPath ({ex.Message})";
        }
    }

    public Task<string?> ExtractAsync(string filePath, string locator, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(locator))
            return Task.FromResult<string?>(null);

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            logger.LogWarning("XmlContentExtractor: file not found: {File}", filePath);
            return Task.FromResult<string?>(null);
        }
        if (info.Length > MaxFileSizeBytes)
            throw new FileContentTooLargeException(filePath, info.Length, MaxFileSizeBytes);

        XDocument doc;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            doc = XDocument.Load(stream);
        }
        catch (XmlException ex)
        {
            logger.LogWarning(ex, "XmlContentExtractor: malformed XML, skipping: {File}", filePath);
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "XmlContentExtractor: could not read file, skipping: {File}", filePath);
            return Task.FromResult<string?>(null);
        }

        // Namespace-blind matching (v1): strip xmlns declarations and prefixes so
        // an operator-written XPath like /file/status/code matches namespaced XML
        // (xmlns="…" or xmlns:ns="…") without local-name() workarounds or per-rule
        // namespace config. Trade-off: explicit namespace-prefixed XPath won't
        // match, and same-local-name-different-namespace elements merge. Real
        // business XML rarely needs that disambiguation; a per-rule
        // NamespaceManager is the v2 escape hatch.
        StripNamespaces(doc);

        object? evaluated;
        try
        {
            evaluated = doc.XPathEvaluate(locator);
        }
        catch (XPathException ex)
        {
            logger.LogWarning(ex, "XmlContentExtractor: invalid XPath '{Locator}' for {File}", locator, filePath);
            return Task.FromResult<string?>(null);
        }

        var value = FirstValue(evaluated)?.Trim();
        return Task.FromResult(string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>
    /// Reduce an XPathEvaluate result to a single string. XPath returns one of:
    /// a node-set (IEnumerable of XObject), or a scalar from a function
    /// (string / double / bool). Order matters — string is itself IEnumerable,
    /// so it must be matched before the IEnumerable arm.
    /// </summary>
    private static string? FirstValue(object? evaluated) => evaluated switch
    {
        null            => null,
        string s        => s,
        bool b          => b ? "true" : "false",
        double d        => d.ToString(CultureInfo.InvariantCulture),
        IEnumerable seq => seq.Cast<object>().Select(NodeValue).FirstOrDefault(v => v is not null),
        _               => null,
    };

    /// <summary>
    /// Rewrites every element name to its local name and drops namespace
    /// declarations + prefixed attributes (keeping attribute local names), so a
    /// plain XPath matches regardless of XML namespaces. Materialised with
    /// ToList() because the descendant walk is lazy and we mutate names in place.
    /// One pass per file at parse time — comfortably within budget at ≤5MB.
    /// </summary>
    private static void StripNamespaces(XDocument doc)
    {
        foreach (var element in doc.Descendants().ToList())
        {
            element.Name = element.Name.LocalName;
            element.ReplaceAttributes(
                element.Attributes()
                    .Where(a => !a.IsNamespaceDeclaration)
                    .Select(a => new XAttribute(a.Name.LocalName, a.Value)));
        }
    }

    private static string? NodeValue(object node) => node switch
    {
        XElement el   => el.Value,
        XAttribute at => at.Value,
        XCData c      => c.Value,   // must precede XText — XCData : XText
        XText t       => t.Value,
        _             => node?.ToString(),
    };
}
